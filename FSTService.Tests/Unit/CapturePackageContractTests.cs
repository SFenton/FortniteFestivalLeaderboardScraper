using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FSTService.Scraping;
using FSTService.Scraping.Replay;

namespace FSTService.Tests.Unit;

public sealed class CapturePackageContractTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CanonicalCatalogResponseAndManifestRoundTrip()
    {
        var first = CreateFixture(reverseEnvelopeInputs: false);
        var second = CreateFixture(reverseEnvelopeInputs: true);
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture =
                CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture =
                CultureInfo.GetCultureInfo("fr-FR");
            var firstManifestBytes = CreateManifestBytes(first);
            var firstRequestBytes =
                await SerializeRequestPlanAsync(
                    first.Definition);

            CultureInfo.CurrentCulture =
                CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture =
                CultureInfo.GetCultureInfo("tr-TR");
            var secondManifestBytes = CreateManifestBytes(second);
            var secondRequestBytes =
                await SerializeRequestPlanAsync(
                    second.Definition);

            Assert.Equal(first.CatalogBytes, second.CatalogBytes);
            Assert.Equal(
                first.ResponseShardBytes,
                second.ResponseShardBytes);
            Assert.Equal(firstManifestBytes, secondManifestBytes);
            Assert.Equal(firstRequestBytes, secondRequestBytes);
            Assert.Equal(
                first.CatalogBytes,
                CapturePackageContract.SerializeCatalog(
                    CapturePackageContract.DeserializeCatalog(
                        first.CatalogBytes)));
            foreach (var response in first.Responses)
            {
                var bytes =
                    CapturePackageContract.SerializeResponse(
                        response);
                Assert.Equal(
                    bytes,
                    CapturePackageContract.SerializeResponse(
                        CapturePackageContract
                            .DeserializeResponse(bytes)));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public async Task CompleteCaptureSealsAndLoadsBoundedResponseShard()
    {
        using var directory =
            new PackageDirectory("capture-complete");
        var fixture = CreateFixture();
        var writer = await CapturePackageWriter.CreateAsync(
            directory.Path,
            fixture.Envelope);
        await AddContentArtifactsAsync(writer, fixture);

        var result = await writer.SealAsync(
            fixture.Definition,
            CreatedAt.AddMinutes(7));
        var tierZero = await TierZeroPackageVerifier.VerifyAsync(
            directory.Path);
        var loaded = await new CapturePackageReader().LoadAsync(
            directory.Path);

        Assert.True(tierZero.IsValid, string.Join(
            Environment.NewLine,
            tierZero.Failures.Select(static failure => failure.Message)));
        Assert.Equal(
            CapturePackageFormat.FormatId,
            result.Manifest.FormatId);
        Assert.Equal(
            fixture.Definition.ResponseShardCount,
            result.Manifest.ResponseShardCount);
        Assert.Equal(
            fixture.Definition.Requests,
            loaded.Requests);
        Assert.Equal(
            fixture.Definition.Scopes,
            loaded.Scopes);
        Assert.True(
            CapturePackageContract.CanonicalEquals(
                fixture.Definition.Catalog,
                loaded.Catalog));
        Assert.Equal(
            result.Envelope.PackageRootHash,
            loaded.Envelope.PackageRootHash);
        Assert.Equal(
            fixture.Definition.ResponseShardCount + 4,
            result.Envelope.Artifacts.Count);
        Assert.Single(
            result.Envelope.Artifacts,
            static artifact =>
                artifact.LogicalOwner ==
                CapturePackageFormat.ResponseShardOwner);
    }

    [Fact]
    public async Task CatalogBytesProveExactSongUniverseAndUnsupportedScopes()
    {
        var fixture = CreateFixture();
        var substitutedCatalog = fixture.Definition.Catalog with
        {
            Songs =
            [
                fixture.Definition.Catalog.Songs[0] with
                {
                    SongId = "song-x",
                },
                fixture.Definition.Catalog.Songs[1] with
                {
                    SongId = "song-y",
                },
            ],
        };
        AssertFailure(
            fixture.Definition with
            {
                Catalog = substitutedCatalog,
            },
            CapturePackageFailureKind.NonCanonicalOrder);

        var songB = fixture.Definition.Catalog.Songs[1];
        var support = songB.ScopeSupport
            .Select(value =>
                value.ScopeKind == CaptureScopeKind.Band &&
                value.LeaderboardType == "Band_Duets"
                    ? value with
                    {
                        Status =
                            CaptureCatalogSupportStatus.Supported,
                    }
                    : value)
            .ToArray();
        AssertFailure(
            fixture.Definition with
            {
                Catalog = fixture.Definition.Catalog with
                {
                    Songs =
                    [
                        fixture.Definition.Catalog.Songs[0],
                        songB with { ScopeSupport = support },
                    ],
                },
            },
            CapturePackageFailureKind.IncompleteCapture);

        var noncanonical = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(fixture.CatalogBytes)
                .Replace(",", ", ", StringComparison.Ordinal));
        var exception = Assert.Throws<CapturePackageException>(() =>
            CapturePackageContract.DeserializeCatalog(
                noncanonical));
        Assert.Equal(
            CapturePackageFailureKind.NonCanonicalJson,
            exception.Kind);

        using var directory =
            new PackageDirectory("capture-catalog-substitution");
        var writer = await CapturePackageWriter.CreateAsync(
            directory.Path,
            fixture.Envelope);
        var substitutedBytes =
            CapturePackageContract.SerializeCatalog(
                substitutedCatalog);
        var substitution = await Assert.ThrowsAsync<
            CapturePackageException>(
            () => writer.AddArtifactAsync(
                new TierZeroArtifactRegistration(
                    CapturePackageFormat.CatalogOwner,
                    CapturePackageFormat.CatalogPath,
                    CapturePackageFormat.JsonMediaType,
                    CapturePackageFormat.CatalogSchemaVersion,
                    substitutedCatalog.SongCount,
                    substitutedBytes.LongLength),
                substitutedBytes));
        Assert.Equal(
            CapturePackageFailureKind.EnvelopeMismatch,
            substitution.Kind);
    }

    [Fact]
    public async Task ResponseShardWriteRejectsInvalidOrNoncanonicalBytes()
    {
        using var directory =
            new PackageDirectory("capture-response-write");
        var fixture = CreateFixture();
        var writer = await CapturePackageWriter.CreateAsync(
            directory.Path,
            fixture.Envelope);
        await writer.AddArtifactAsync(
            CatalogRegistration(fixture),
            fixture.CatalogBytes);

        var empty = await Assert.ThrowsAsync<CapturePackageException>(
            () => writer.AddArtifactAsync(
                ResponseRegistration(
                    CapturePackageFormat.ResponseShardPath(0),
                    1,
                    0),
                ReadOnlyMemory<byte>.Empty));
        Assert.Equal(
            CapturePackageFailureKind.ArtifactMismatch,
            empty.Kind);

        var arbitrary = Encoding.UTF8.GetBytes("{}\n");
        var arbitraryException =
            await Assert.ThrowsAsync<CapturePackageException>(
                () => writer.AddArtifactAsync(
                    ResponseRegistration(
                        CapturePackageFormat.ResponseShardPath(0),
                        1,
                        arbitrary.Length),
                    arbitrary));
        Assert.Equal(
            CapturePackageFailureKind.InvalidMetadata,
            arbitraryException.Kind);

        var canonical =
            CapturePackageContract.SerializeResponse(
                fixture.Responses[0]);
        var noncanonical = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(canonical)
                .Replace(",", ", ", StringComparison.Ordinal) +
            "\n");
        var noncanonicalException =
            await Assert.ThrowsAsync<CapturePackageException>(
                () => writer.AddArtifactAsync(
                    ResponseRegistration(
                        CapturePackageFormat.ResponseShardPath(0),
                        1,
                        noncanonical.Length),
                    noncanonical));
        Assert.Equal(
            CapturePackageFailureKind.NonCanonicalJson,
            noncanonicalException.Kind);

        var incoherent = TierZeroCanonicalJson.Serialize(
            fixture.Responses[2] with
            {
                ProviderReportedTotalPages = 1,
            });
        var incoherentBytes = CombineJsonLines([incoherent]);
        var semanticException =
            await Assert.ThrowsAsync<CapturePackageException>(
                () => writer.AddArtifactAsync(
                    ResponseRegistration(
                        CapturePackageFormat.ResponseShardPath(0),
                        1,
                        incoherentBytes.Length),
                    incoherentBytes));
        Assert.Equal(
            CapturePackageFailureKind.AggregateMismatch,
            semanticException.Kind);
    }

    [Fact]
    public void StructuralPreflightRejectsAliasedArraysAndDuplicateEntryFields()
    {
        var fixture = CreateFixture();
        var canonicalResponse =
            Encoding.UTF8.GetString(
                CapturePackageContract.SerializeResponse(
                    fixture.Responses[0]));
        var aliasedEntries = Encoding.UTF8.GetBytes(
            canonicalResponse.Replace(
                "\"entries\":",
                "\"Entries\":",
                StringComparison.Ordinal));
        var aliasException = Assert.Throws<CapturePackageException>(
            () => CapturePackageContract.DeserializeResponse(
                aliasedEntries));
        Assert.Equal(
            CapturePackageFailureKind.NonCanonicalJson,
            aliasException.Kind);

        var duplicateEntries = Encoding.UTF8.GetBytes(
            "{\"entries\":[]," + canonicalResponse[1..]);
        var duplicateArrayException =
            Assert.Throws<CapturePackageException>(
                () => CapturePackageContract.DeserializeResponse(
                    duplicateEntries));
        Assert.Equal(
            CapturePackageFailureKind.NonCanonicalJson,
            duplicateArrayException.Kind);

        var canonicalCatalog =
            Encoding.UTF8.GetString(fixture.CatalogBytes);
        var aliasedSupport = Encoding.UTF8.GetBytes(
            canonicalCatalog.Replace(
                "\"scopeSupport\":",
                "\"ScopeSupport\":",
                StringComparison.Ordinal));
        var catalogAliasException =
            Assert.Throws<CapturePackageException>(
                () => CapturePackageContract.DeserializeCatalog(
                    aliasedSupport));
        Assert.Equal(
            CapturePackageFailureKind.NonCanonicalJson,
            catalogAliasException.Kind);

        using var duplicateEntryDocument =
            JsonDocument.Parse("""{"rank":1,"rank":2}""");
        var duplicateEntryResponse =
            fixture.Responses[0] with
            {
                EntryCount = 1,
                Entries =
                [
                    duplicateEntryDocument.RootElement.Clone(),
                ],
            };
        var duplicateEntryException =
            Assert.Throws<CapturePackageException>(
                () => CapturePackageContract.SerializeResponse(
                    duplicateEntryResponse));
        Assert.Equal(
            CapturePackageFailureKind.NonCanonicalJson,
            duplicateEntryException.Kind);
    }

    [Fact]
    public async Task ResponseSemanticsAreCrossCheckedOnSealAndRead()
    {
        var original = CreateFixture();
        var wrongResponse = original.Responses[0] with
        {
            SongId = "song-b",
        };
        var invalid = ReplaceResponses(
            original,
            [
                wrongResponse,
                .. original.Responses.Skip(1),
            ]);

        using (var directory =
               new PackageDirectory("capture-response-seal"))
        {
            var writer = await CapturePackageWriter.CreateAsync(
                directory.Path,
                invalid.Envelope);
            await AddContentArtifactsAsync(writer, invalid);

            var exception =
                await Assert.ThrowsAsync<CapturePackageException>(
                    () => writer.SealAsync(
                        invalid.Definition,
                        CreatedAt.AddMinutes(7)));

            Assert.Equal(
                CapturePackageFailureKind.ArtifactMismatch,
                exception.Kind);
            Assert.False(File.Exists(Path.Combine(
                directory.Path,
                TierZeroEvidenceFormat.ManifestFileName)));
        }

        using (var directory =
               new PackageDirectory("capture-response-read"))
        {
            await SealUncheckedCaptureAsync(
                directory.Path,
                invalid);
            var exception =
                await Assert.ThrowsAsync<CapturePackageException>(
                    () => new CapturePackageReader().LoadAsync(
                        directory.Path));
            Assert.Equal(
                CapturePackageFailureKind.ArtifactMismatch,
                exception.Kind);
        }
    }

    [Fact]
    public void ZeroPageDiscoveryIsExplicitAndAllUnsupportedIsRejected()
    {
        var fixture = CreateFixture();
        var zeroScope = fixture.Definition.Scopes[2];
        var zeroRequest = fixture.Definition.Requests[2];

        Assert.Equal(0, zeroScope.DeclaredPageCount);
        Assert.Equal(0, zeroScope.DeclaredEntryCount);
        Assert.Equal(1, zeroScope.CapturedPageCount);
        Assert.Equal(0, zeroRequest.ProviderReportedTotalPages);
        Assert.Equal(0, zeroRequest.ProviderReportedTotalEntries);
        Assert.Equal(0, zeroRequest.CapturedEntryCount);
        _ = CapturePackageContract.ValidateDefinition(
            fixture.Definition);

        var catalog = CreateCatalog(
            static (_, _, _) =>
                CaptureCatalogSupportStatus.Unsupported);
        var scopes = catalog.Songs
            .Select((song, ordinal) =>
                new CaptureScopeDescriptor(
                    ordinal,
                    song.SongId,
                    CaptureScopeKind.Solo,
                    "Solo_Guitar",
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    CaptureScopeStatus.Unsupported,
                    CapturePackageContract
                        .ComputeScopeContentSha256(
                            Array.Empty<
                                CaptureRequestDescriptor>())))
            .ToArray();
        var allUnsupported = new CapturePackageDefinition(
            "capture-all-unsupported",
            catalog,
            ["Solo_Guitar"],
            [],
            CreatedAt.AddMinutes(1),
            CreatedAt.AddMinutes(2),
            scopes.Length,
            0,
            0,
            0,
            0,
            0,
            [],
            scopes,
            CapturePackageStatus.Complete);

        AssertFailure(
            allUnsupported,
            CapturePackageFailureKind.IncompleteCapture);
    }

    [Fact]
    public async Task ShardLayoutRejectsGapsPathsAndDuplicateMembers()
    {
        var fixture = CreateFixture();
        var gapRequests = fixture.Definition.Requests.ToArray();
        gapRequests[1] = gapRequests[1] with
        {
            ResponseOffset =
                gapRequests[1].ResponseOffset + 1,
        };
        AssertFailure(
            RebuildDefinition(fixture, gapRequests),
            CapturePackageFailureKind.AggregateMismatch);

        var pathRequests = fixture.Definition.Requests.ToArray();
        pathRequests[0] = pathRequests[0] with
        {
            ResponseShardPath =
                "capture/responses/0.jsonl",
        };
        AssertFailure(
            fixture.Definition with
            {
                Requests = pathRequests,
            },
            CapturePackageFailureKind.InvalidMetadata);
        var missingPathRequests =
            fixture.Definition.Requests.ToArray();
        missingPathRequests[0] = missingPathRequests[0] with
        {
            ResponseShardPath = null!,
        };
        AssertFailure(
            fixture.Definition with
            {
                Requests = missingPathRequests,
            },
            CapturePackageFailureKind.InvalidMetadata);

        using var directory =
            new PackageDirectory("capture-duplicate-member");
        var writer = await CapturePackageWriter.CreateAsync(
            directory.Path,
            fixture.Envelope);
        await writer.AddArtifactAsync(
            CatalogRegistration(fixture),
            fixture.CatalogBytes);
        var duplicateBytes = CombineJsonLines(
        [
            CapturePackageContract.SerializeResponse(
                fixture.Responses[0]),
            CapturePackageContract.SerializeResponse(
                fixture.Responses[0]),
        ]);
        var duplicate = await Assert.ThrowsAsync<
            CapturePackageException>(
            () => writer.AddArtifactAsync(
                ResponseRegistration(
                    CapturePackageFormat.ResponseShardPath(0),
                    2,
                    duplicateBytes.Length),
                duplicateBytes));
        Assert.Equal(
            CapturePackageFailureKind.NonCanonicalOrder,
            duplicate.Kind);

        var overflow = CreateScaleDefinition(
            scopeCount: 1,
            pagesPerScope: 2,
            responsesPerShard: 2,
            responseLength: 256);
        var overflowRequests = overflow.Requests
            .Select(request => request with
            {
                CapturedRequestCount = long.MaxValue,
            })
            .ToArray();
        var overflowScope = overflow.Scopes[0] with
        {
            CapturedRequestCount = long.MaxValue,
            ContentSha256 =
                CapturePackageContract.ComputeScopeContentSha256(
                    overflowRequests),
        };
        AssertFailure(
            overflow with
            {
                TotalRequestCount = long.MaxValue,
                Requests = overflowRequests,
                Scopes = [overflowScope],
            },
            CapturePackageFailureKind.AggregateMismatch);
    }

    [Fact]
    public void ManifestRecordCeilingsRejectCounterfeitCounts()
    {
        var fixture = CreateFixture();
        var manifest = CreateManifest(fixture);
        var forged = manifest with
        {
            TotalPageCount = int.MaxValue,
            RequestPlan = manifest.RequestPlan with
            {
                RecordCount = int.MaxValue,
                Bytes = 3,
            },
            ManifestRootHash = null,
        };
        var rootHash = Hash(
            TierZeroCanonicalJson.Serialize(forged));
        var bytes = TierZeroCanonicalJson.Serialize(
            forged with { ManifestRootHash = rootHash });

        var exception = Assert.Throws<CapturePackageException>(
            () => CapturePackageContract.DeserializeManifest(bytes));

        Assert.Equal(
            CapturePackageFailureKind.RecordLimitExceeded,
            exception.Kind);
    }

    [Fact]
    public void StorageAdmissionReportsCommittedAndProjectedStates()
    {
        var policy = new CapturePackageStoragePolicy(
            MaximumPackageBytes: 1_000,
            MinimumFreeSpaceReserveBytes: 500,
            MaximumRetainedSealedPackages: 1);
        var accepted = CapturePackageStorageAdmission.Evaluate(
            policy,
            new CapturePackageStorageState(
                ProposedPackageBytes: 400,
                AvailableFreeSpaceBytes: 1_000,
                RetainedSealedPackageCount: 0));

        Assert.True(accepted.IsAdmitted);
        Assert.Empty(accepted.Rejections);
        Assert.Equal(
            600,
            accepted.CommittedAvailableFreeSpaceBytes);
        Assert.Equal(
            1,
            accepted.CommittedRetainedSealedPackageCount);
        Assert.Equal(
            600,
            accepted.ProjectedRemainingFreeSpaceBytesIfAdmitted);
        Assert.Equal(
            1,
            accepted.ProjectedRetainedSealedPackageCountIfAdmitted);

        var rejected = CapturePackageStorageAdmission.Evaluate(
            policy,
            new CapturePackageStorageState(
                ProposedPackageBytes: 1_001,
                AvailableFreeSpaceBytes: 1_200,
                RetainedSealedPackageCount: 1));
        Assert.False(rejected.IsAdmitted);
        Assert.Equal(
            1_200,
            rejected.CommittedAvailableFreeSpaceBytes);
        Assert.Equal(
            1,
            rejected.CommittedRetainedSealedPackageCount);
        Assert.Equal(
            199,
            rejected.ProjectedRemainingFreeSpaceBytesIfAdmitted);
        Assert.Equal(
            2,
            rejected.ProjectedRetainedSealedPackageCountIfAdmitted);
    }

    [Fact]
    public async Task ConcurrentContentWriterCannotSlipPastAtomicSeal()
    {
        using var directory =
            new PackageDirectory("capture-seal-race");
        var fixture = CreateFixture();
        var sealingWriter = await CapturePackageWriter.CreateAsync(
            directory.Path,
            fixture.Envelope);
        await AddContentArtifactsAsync(
            sealingWriter,
            fixture);
        var competingWriter = await CapturePackageWriter.ResumeAsync(
            directory.Path,
            ResumeExpectations(fixture.Envelope));
        var insideLock = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLock = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var sealTask = sealingWriter.SealAsync(
            fixture.Definition,
            CreatedAt.AddMinutes(7),
            async cancellationToken =>
            {
                insideLock.TrySetResult();
                await releaseLock.Task.WaitAsync(cancellationToken);
            });
        await insideLock.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var extraResponse = fixture.Responses[0] with
        {
            RequestOrdinal = 99,
            ScopeOrdinal = 99,
            ScopeRequestOrdinal = 0,
        };
        var extraBytes = CombineJsonLines(
        [
            CapturePackageContract.SerializeResponse(
                extraResponse),
        ]);
        var addTask = competingWriter.AddArtifactAsync(
            ResponseRegistration(
                CapturePackageFormat.ResponseShardPath(1),
                1,
                extraBytes.Length),
            extraBytes);
        await Task.Delay(100);
        Assert.False(addTask.IsCompleted);

        releaseLock.TrySetResult();
        var sealedPackage = await sealTask;
        var addFailure = await Assert.ThrowsAsync<
            TierZeroPackageException>(
            async () => await addTask);

        Assert.Equal(
            TierZeroPackageError.PackageAlreadySealed,
            addFailure.Error);
        Assert.DoesNotContain(
            sealedPackage.Envelope.Artifacts,
            static artifact =>
                artifact.Path ==
                "capture/responses/0001.jsonl");
        Assert.Equal(
            fixture.Definition.ResponseShardCount + 4,
            sealedPackage.Envelope.Artifacts.Count);
    }

    [Fact]
    public async Task StaleWriterRechecksContentGuardAfterLockedRefresh()
    {
        using var directory =
            new PackageDirectory("capture-content-guard");
        var fixture = CreateFixture();
        var staleWriter = await CapturePackageWriter.CreateAsync(
            directory.Path,
            fixture.Envelope);
        await AddContentArtifactsAsync(
            staleWriter,
            fixture);
        var tierZeroWriter = await TierZeroPackageWriter.ResumeAsync(
            directory.Path,
            ResumeExpectations(fixture.Envelope));
        var requestBytes =
            await SerializeRequestPlanAsync(
                fixture.Definition);
        await tierZeroWriter.AddArtifactAsync(
            new TierZeroArtifactRegistration(
                CapturePackageFormat.RequestPlanOwner,
                CapturePackageFormat.RequestPlanPath,
                CapturePackageFormat.JsonLinesMediaType,
                CapturePackageFormat.DescriptorSchemaVersion,
                fixture.Definition.Requests.Count,
                requestBytes.LongLength),
            requestBytes);

        var extraResponse = fixture.Responses[0] with
        {
            RequestOrdinal = 99,
            ScopeOrdinal = 99,
        };
        var extraBytes = CombineJsonLines(
        [
            CapturePackageContract.SerializeResponse(
                extraResponse),
        ]);
        var exception = await Assert.ThrowsAsync<
            CapturePackageException>(
            () => staleWriter.AddArtifactAsync(
                ResponseRegistration(
                    CapturePackageFormat.ResponseShardPath(1),
                    1,
                    extraBytes.Length),
                extraBytes));

        Assert.Equal(
            CapturePackageFailureKind.ArtifactMismatch,
            exception.Kind);
        Assert.False(File.Exists(Path.Combine(
            directory.Path,
            CapturePackageFormat.ResponseShardPath(1).Replace(
                '/',
                Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task ResumeFinishesPartiallyWrittenCanonicalMetadata()
    {
        using var directory =
            new PackageDirectory("capture-resume");
        var fixture = CreateFixture();
        var tierZero = await TierZeroPackageWriter.CreateAsync(
            directory.Path,
            fixture.Envelope);
        await AddContentArtifactsAsync(tierZero, fixture);
        var requestBytes =
            await SerializeRequestPlanAsync(
                fixture.Definition);
        await tierZero.AddArtifactAsync(
            new TierZeroArtifactRegistration(
                CapturePackageFormat.RequestPlanOwner,
                CapturePackageFormat.RequestPlanPath,
                CapturePackageFormat.JsonLinesMediaType,
                CapturePackageFormat.DescriptorSchemaVersion,
                fixture.Definition.Requests.Count,
                requestBytes.LongLength),
            requestBytes);
        await tierZero.MarkInterruptedAsync(
            "synthetic capture interruption",
            CreatedAt.AddMinutes(6));

        var resumed = await CapturePackageWriter.ResumeAsync(
            directory.Path,
            ResumeExpectations(fixture.Envelope));
        var result = await resumed.SealAsync(
            fixture.Definition,
            CreatedAt.AddMinutes(7));

        Assert.Equal(
            TierZeroPackageStatus.Sealed,
            result.Envelope.Status);
        Assert.True((await TierZeroPackageVerifier.VerifyAsync(
            directory.Path)).IsValid);
    }

    [Fact]
    public async Task MissingResponseShardCannotSeal()
    {
        using var directory =
            new PackageDirectory("capture-missing-shard");
        var fixture = CreateFixture();
        var writer = await CapturePackageWriter.CreateAsync(
            directory.Path,
            fixture.Envelope);
        await writer.AddArtifactAsync(
            CatalogRegistration(fixture),
            fixture.CatalogBytes);

        var exception =
            await Assert.ThrowsAsync<CapturePackageException>(
                () => writer.SealAsync(
                    fixture.Definition,
                    CreatedAt.AddMinutes(7)));

        Assert.Equal(
            CapturePackageFailureKind.ArtifactMismatch,
            exception.Kind);
        Assert.False(File.Exists(Path.Combine(
            directory.Path,
            TierZeroEvidenceFormat.ManifestFileName)));
    }

    [Fact]
    public async Task TierZeroVerifierDetectsCaptureShardTampering()
    {
        using var directory =
            new PackageDirectory("capture-tamper");
        var fixture = CreateFixture();
        var writer = await CapturePackageWriter.CreateAsync(
            directory.Path,
            fixture.Envelope);
        await AddContentArtifactsAsync(writer, fixture);
        await writer.SealAsync(
            fixture.Definition,
            CreatedAt.AddMinutes(7));
        var responsePath = Path.Combine(
            directory.Path,
            CapturePackageFormat.ResponseShardPath(0).Replace(
                '/',
                Path.DirectorySeparatorChar));
        var bytes = await File.ReadAllBytesAsync(responsePath);
        bytes[0] ^= 0x01;
        await File.WriteAllBytesAsync(responsePath, bytes);

        var verification =
            await TierZeroPackageVerifier.VerifyAsync(directory.Path);

        Assert.False(verification.IsValid);
        Assert.Contains(
            verification.Failures,
            static failure =>
                failure.Kind ==
                TierZeroVerificationFailureKind.ArtifactHashMismatch);
    }

    [Fact]
    public async Task ReaderUsesStructurePreflightThenSemanticHashing()
    {
        using var directory =
            new PackageDirectory("capture-bounded-preflight");
        var fixture = CreateFixture();
        var writer = await CapturePackageWriter.CreateAsync(
            directory.Path,
            fixture.Envelope);
        await AddContentArtifactsAsync(writer, fixture);
        await writer.SealAsync(
            fixture.Definition,
            CreatedAt.AddMinutes(7));
        var responsePath = Path.Combine(
            directory.Path,
            CapturePackageFormat.ResponseShardPath(0).Replace(
                '/',
                Path.DirectorySeparatorChar));
        var bytes = await File.ReadAllBytesAsync(responsePath);
        bytes[0] ^= 0x01;
        await File.WriteAllBytesAsync(responsePath, bytes);

        var structure =
            await TierZeroPackageVerifier.VerifyStructureAsync(
                directory.Path);
        var full = await TierZeroPackageVerifier.VerifyAsync(
            directory.Path);
        var readerException =
            await Assert.ThrowsAsync<CapturePackageException>(
                () => new CapturePackageReader().LoadAsync(
                    directory.Path));

        Assert.True(structure.IsValid);
        Assert.False(full.IsValid);
        Assert.Equal(
            CapturePackageFailureKind.InvalidMetadata,
            readerException.Kind);
    }

    [Fact]
    public async Task ReaderRejectsNoncanonicalDescriptorArtifactMetadata()
    {
        using var directory =
            new PackageDirectory("capture-metadata-owner");
        var fixture = CreateFixture();
        await SealUncheckedCaptureAsync(
            directory.Path,
            fixture,
            requestPlanOwner: "incorrect-owner");

        var exception =
            await Assert.ThrowsAsync<CapturePackageException>(
                () => new CapturePackageReader().LoadAsync(
                    directory.Path));

        Assert.Equal(
            CapturePackageFailureKind.ArtifactMismatch,
            exception.Kind);
    }

    [Fact]
    public void CaptureInventoryPreflightFailsFastAtEntryLimit()
    {
        using var directory =
            new PackageDirectory("capture-entry-limit");
        Directory.CreateDirectory(directory.Path);
        File.WriteAllBytes(
            Path.Combine(directory.Path, "one"),
            [1]);
        File.WriteAllBytes(
            Path.Combine(directory.Path, "two"),
            [2]);
        File.WriteAllBytes(
            Path.Combine(directory.Path, "three"),
            [3]);

        var exception = Assert.Throws<TierZeroPackageException>(
            () => TierZeroPackageFileEnumerator.Enumerate(
                directory.Path,
                maximumEntries: 2));

        Assert.Equal(
            TierZeroPackageError.InvalidMetadata,
            exception.Error);
    }

    [Fact]
    public async Task StructureVerificationUsesTheSuppliedInventory()
    {
        using var directory =
            new PackageDirectory("capture-inventory-binding");
        var fixture = CreateFixture();
        var writer = await CapturePackageWriter.CreateAsync(
            directory.Path,
            fixture.Envelope);
        await AddContentArtifactsAsync(writer, fixture);
        await writer.SealAsync(
            fixture.Definition,
            CreatedAt.AddMinutes(7));
        var manifestPath = Path.Combine(
            directory.Path,
            TierZeroEvidenceFormat.ManifestFileName);
        var heldManifestPath =
            manifestPath + ".held";
        File.Move(manifestPath, heldManifestPath);
        var inventory =
            TierZeroPackageFileEnumerator.Enumerate(
                directory.Path,
                CapturePackageFormat
                    .MaximumPackageFileSystemEntries);
        File.Move(heldManifestPath, manifestPath);

        var verification =
            await TierZeroPackageVerifier.VerifyStructureAsync(
                directory.Path,
                inventory,
                maximumFileSystemEntries:
                    CapturePackageFormat
                        .MaximumPackageFileSystemEntries);

        Assert.False(verification.IsValid);
        Assert.Contains(
            verification.Failures,
            static failure =>
                failure.Kind ==
                TierZeroVerificationFailureKind.UnsealedPackage);
    }

    [Fact]
    public void CatalogValidationAndParserRejectMalformedInputs()
    {
        var fixture = CreateFixture();
        var catalog = fixture.Definition.Catalog;
        var firstSong = catalog.Songs[0];
        var secondSong = catalog.Songs[1];
        var firstSupport = firstSong.ScopeSupport.ToArray();
        var reversedSupport = firstSupport.Reverse().ToArray();
        var nullSupport = firstSupport.ToArray();
        nullSupport[0] = null!;
        var invalidStatusSupport = firstSupport.ToArray();
        invalidStatusSupport[0] = invalidStatusSupport[0] with
        {
            Status = (CaptureCatalogSupportStatus)999,
        };

        var cases = new[]
        {
            (
                "missing songs",
                catalog with { Songs = null! },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "unsupported catalog format",
                catalog with { FormatId = "unsupported" },
                CapturePackageFailureKind.UnsupportedFormat),
            (
                "nonpositive catalog version",
                catalog with { CatalogVersion = 0 },
                CapturePackageFailureKind.NegativeCount),
            (
                "catalog song count mismatch",
                catalog with { SongCount = catalog.SongCount + 1 },
                CapturePackageFailureKind.AggregateMismatch),
            (
                "null catalog song",
                catalog with
                {
                    Songs =
                    [
                        null!,
                        secondSong,
                    ],
                },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "null support collection",
                catalog with
                {
                    Songs =
                    [
                        firstSong with { ScopeSupport = null! },
                        secondSong,
                    ],
                },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "unsafe song ID",
                catalog with
                {
                    Songs =
                    [
                        firstSong with { SongId = " " },
                        secondSong,
                    ],
                },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "secret-like song ID",
                catalog with
                {
                    Songs =
                    [
                        firstSong with
                        {
                            SongId = "localhost:5432",
                        },
                        secondSong,
                    ],
                },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "duplicate song ID",
                catalog with
                {
                    Songs =
                    [
                        firstSong,
                        secondSong with
                        {
                            SongId = firstSong.SongId,
                        },
                    ],
                },
                CapturePackageFailureKind.DuplicateScope),
            (
                "noncanonical song order",
                catalog with
                {
                    Songs =
                    [
                        secondSong,
                        firstSong,
                    ],
                },
                CapturePackageFailureKind.NonCanonicalOrder),
            (
                "incomplete support set",
                catalog with
                {
                    Songs =
                    [
                        firstSong with
                        {
                            ScopeSupport =
                                firstSupport[..^1],
                        },
                        secondSong,
                    ],
                },
                CapturePackageFailureKind.MissingScope),
            (
                "null support record",
                catalog with
                {
                    Songs =
                    [
                        firstSong with
                        {
                            ScopeSupport = nullSupport,
                        },
                        secondSong,
                    ],
                },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "unknown support status",
                catalog with
                {
                    Songs =
                    [
                        firstSong with
                        {
                            ScopeSupport =
                                invalidStatusSupport,
                        },
                        secondSong,
                    ],
                },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "noncanonical support order",
                catalog with
                {
                    Songs =
                    [
                        firstSong with
                        {
                            ScopeSupport = reversedSupport,
                        },
                        secondSong,
                    ],
                },
                CapturePackageFailureKind.NonCanonicalOrder),
        };

        foreach (var (name, value, expected) in cases)
        {
            AssertCaptureFailure(
                name,
                () => CapturePackageContract.ValidateCatalog(value),
                expected);
        }

        var canonical =
            Encoding.UTF8.GetString(fixture.CatalogBytes);
        var parserCases = new[]
        {
            (
                "empty catalog bytes",
                Array.Empty<byte>(),
                CapturePackageFailureKind.RecordLimitExceeded),
            (
                "invalid catalog JSON",
                Encoding.UTF8.GetBytes("{"),
                CapturePackageFailureKind.InvalidMetadata),
            (
                "catalog property has an invalid JSON type",
                Encoding.UTF8.GetBytes(
                    canonical.Replace(
                        "\"catalogVersion\":42",
                        "\"catalogVersion\":\"invalid\"",
                        StringComparison.Ordinal)),
                CapturePackageFailureKind.InvalidMetadata),
            (
                "catalog root is not an object",
                Encoding.UTF8.GetBytes("[]"),
                CapturePackageFailureKind.InvalidMetadata),
            (
                "catalog songs are missing",
                Encoding.UTF8.GetBytes("{}"),
                CapturePackageFailureKind.InvalidMetadata),
            (
                "catalog songs are not an array",
                Encoding.UTF8.GetBytes("""{"songs":{}}"""),
                CapturePackageFailureKind.InvalidMetadata),
            (
                "catalog nested support is not an array",
                Encoding.UTF8.GetBytes(
                    """{"songs":[{"scopeSupport":{}}]}"""),
                CapturePackageFailureKind.InvalidMetadata),
            (
                "catalog has trailing JSON",
                Encoding.UTF8.GetBytes(canonical + "{}"),
                CapturePackageFailureKind.InvalidMetadata),
            (
                "catalog is not canonical",
                Encoding.UTF8.GetBytes(
                    canonical.Replace(
                        ",",
                        ", ",
                        StringComparison.Ordinal)),
                CapturePackageFailureKind.NonCanonicalJson),
        };

        foreach (var (name, bytes, expected) in parserCases)
        {
            AssertCaptureFailure(
                name,
                () => CapturePackageContract.DeserializeCatalog(bytes),
                expected);
        }
    }

    [Fact]
    public void ResponseValidationAndParserRejectMalformedInputs()
    {
        var fixture = CreateFixture();
        var response = fixture.Responses[0];
        var scalarEntry = Entry("1");

        var cases = new[]
        {
            (
                "missing response entries",
                response with { Entries = null! },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "unsupported response format",
                response with { FormatId = "unsupported" },
                CapturePackageFailureKind.UnsupportedFormat),
            (
                "negative request ordinal",
                response with { RequestOrdinal = -1 },
                CapturePackageFailureKind.NegativeCount),
            (
                "negative scope ordinal",
                response with { ScopeOrdinal = -1 },
                CapturePackageFailureKind.NegativeCount),
            (
                "negative scope request ordinal",
                response with { ScopeRequestOrdinal = -1 },
                CapturePackageFailureKind.NegativeCount),
            (
                "unsafe response song ID",
                response with { SongId = " " },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "unknown response scope kind",
                response with
                {
                    ScopeKind = (CaptureScopeKind)999,
                },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "unknown response kind",
                response with
                {
                    ResponseKind = (CaptureResponseKind)999,
                },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "response kind does not match scope",
                response with
                {
                    ResponseKind =
                        CaptureResponseKind.BandLeaderboardPage,
                },
                CapturePackageFailureKind.AggregateMismatch),
            (
                "unknown response leaderboard",
                response with
                {
                    LeaderboardType = "Solo_Unknown",
                },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "negative response page",
                response with { PageIndex = -1 },
                CapturePackageFailureKind.NegativeCount),
            (
                "invalid response page size",
                response with { PageSize = 0 },
                CapturePackageFailureKind.RecordLimitExceeded),
            (
                "negative provider page total",
                response with
                {
                    ProviderReportedTotalPages = -1,
                },
                CapturePackageFailureKind.NegativeCount),
            (
                "negative provider entry total",
                response with
                {
                    ProviderReportedTotalEntries = -1,
                },
                CapturePackageFailureKind.NegativeCount),
            (
                "provider page total exceeds limit",
                response with
                {
                    ProviderReportedTotalPages =
                        CapturePackageFormat
                            .MaximumRequestRecords + 1,
                },
                CapturePackageFailureKind.RecordLimitExceeded),
            (
                "response entry count mismatch",
                response with { EntryCount = 1 },
                CapturePackageFailureKind.AggregateMismatch),
            (
                "response entry is not an object",
                response with
                {
                    EntryCount = 1,
                    Entries = [scalarEntry],
                },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "response zero-universe totals conflict",
                response with
                {
                    ProviderReportedTotalPages = 0,
                },
                CapturePackageFailureKind.AggregateMismatch),
            (
                "response page exceeds provider total",
                response with
                {
                    PageIndex = 1,
                },
                CapturePackageFailureKind.AggregateMismatch),
            (
                "response entries exceed provider total",
                response with
                {
                    ProviderReportedTotalEntries = 1,
                },
                CapturePackageFailureKind.AggregateMismatch),
        };

        foreach (var (name, value, expected) in cases)
        {
            AssertCaptureFailure(
                name,
                () => CapturePackageContract.ValidateResponse(value),
                expected);
        }

        var canonical = Encoding.UTF8.GetString(
            CapturePackageContract.SerializeResponse(response));
        var parserCases = new[]
        {
            (
                "empty response bytes",
                Array.Empty<byte>(),
                CapturePackageFailureKind.RecordLimitExceeded),
            (
                "invalid response JSON",
                Encoding.UTF8.GetBytes("{"),
                CapturePackageFailureKind.InvalidMetadata),
            (
                "response property has an invalid JSON type",
                Encoding.UTF8.GetBytes(
                    canonical.Replace(
                        "\"requestOrdinal\":0",
                        "\"requestOrdinal\":\"invalid\"",
                        StringComparison.Ordinal)),
                CapturePackageFailureKind.InvalidMetadata),
            (
                "response root is not an object",
                Encoding.UTF8.GetBytes("[]"),
                CapturePackageFailureKind.InvalidMetadata),
            (
                "response entries are missing",
                Encoding.UTF8.GetBytes("{}"),
                CapturePackageFailureKind.InvalidMetadata),
            (
                "response entries are not an array",
                Encoding.UTF8.GetBytes("""{"entries":{}}"""),
                CapturePackageFailureKind.InvalidMetadata),
            (
                "response has trailing JSON",
                Encoding.UTF8.GetBytes(canonical + "{}"),
                CapturePackageFailureKind.InvalidMetadata),
            (
                "response is not canonical",
                Encoding.UTF8.GetBytes(
                    canonical.Replace(
                        ",",
                        ", ",
                        StringComparison.Ordinal)),
                CapturePackageFailureKind.NonCanonicalJson),
        };

        foreach (var (name, bytes, expected) in parserCases)
        {
            AssertCaptureFailure(
                name,
                () => CapturePackageContract.DeserializeResponse(bytes),
                expected);
        }
    }

    [Fact]
    public void DefinitionValidationRejectsInvalidMetadataAndCounts()
    {
        var fixture = CreateFixture();
        var definition = fixture.Definition;
        var scopes = definition.Scopes.ToArray();
        var requests = definition.Requests.ToArray();

        var nullScopes = scopes.ToArray();
        nullScopes[0] = null!;
        var nullRequests = requests.ToArray();
        nullRequests[0] = null!;
        var duplicateScopes = scopes.ToArray();
        duplicateScopes[1] = duplicateScopes[1] with
        {
            Ordinal = duplicateScopes[0].Ordinal,
        };
        var reversedScopes = scopes.ToArray();
        (reversedScopes[0], reversedScopes[1]) =
            (reversedScopes[1], reversedScopes[0]);
        var duplicateRequests = requests.ToArray();
        duplicateRequests[1] = duplicateRequests[1] with
        {
            Ordinal = duplicateRequests[0].Ordinal,
        };
        var reversedRequests = requests.ToArray();
        (reversedRequests[0], reversedRequests[1]) =
            (reversedRequests[1], reversedRequests[0]);

        var cases = new[]
        {
            (
                "missing definition catalog",
                definition with { Catalog = null! },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "unsafe capture ID",
                definition with { CaptureId = " " },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "secret-like capture ID",
                definition with
                {
                    CaptureId = "localhost:5432",
                },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "unknown package status",
                definition with
                {
                    Status = (CapturePackageStatus)999,
                },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "incomplete package status",
                definition with
                {
                    Status = CapturePackageStatus.Incomplete,
                },
                CapturePackageFailureKind.IncompleteCapture),
            (
                "null enabled solo collection",
                definition with
                {
                    EnabledSoloInstruments = null!,
                },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "null enabled solo value",
                definition with
                {
                    EnabledSoloInstruments = [null!],
                },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "duplicate enabled solo value",
                definition with
                {
                    EnabledSoloInstruments =
                    [
                        "Solo_Guitar",
                        "Solo_Guitar",
                    ],
                },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "unsupported enabled solo value",
                definition with
                {
                    EnabledSoloInstruments = ["Solo_Unknown"],
                },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "noncanonical enabled solo order",
                definition with
                {
                    EnabledSoloInstruments =
                    [
                        "Solo_Bass",
                        "Solo_Guitar",
                    ],
                },
                CapturePackageFailureKind.NonCanonicalOrder),
            (
                "no enabled scope types",
                definition with
                {
                    EnabledSoloInstruments = [],
                    EnabledBandTypes = [],
                },
                CapturePackageFailureKind.MissingScope),
            (
                "missing capture start",
                definition with
                {
                    CaptureStartedAtUtc = default,
                },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "missing capture completion",
                definition with
                {
                    CaptureCompletedAtUtc = default,
                },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "capture completion precedes start",
                definition with
                {
                    CaptureCompletedAtUtc =
                        definition.CaptureStartedAtUtc
                            .AddTicks(-1),
                },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "invalid total scope count",
                definition with { TotalScopeCount = 0 },
                CapturePackageFailureKind.RecordLimitExceeded),
            (
                "negative total entry count",
                definition with { TotalEntryCount = -1 },
                CapturePackageFailureKind.NegativeCount),
            (
                "zero total page count",
                definition with { TotalPageCount = 0 },
                CapturePackageFailureKind.IncompleteCapture),
            (
                "total page count exceeds limit",
                definition with
                {
                    TotalPageCount =
                        CapturePackageFormat
                            .MaximumRequestRecords + 1,
                },
                CapturePackageFailureKind.RecordLimitExceeded),
            (
                "response shard count exceeds limit",
                definition with
                {
                    ResponseShardCount =
                        CapturePackageFormat
                            .MaximumResponseShards + 1,
                },
                CapturePackageFailureKind.RecordLimitExceeded),
            (
                "empty scope descriptors",
                definition with { Scopes = [] },
                CapturePackageFailureKind.RecordLimitExceeded),
            (
                "null scope descriptor",
                definition with { Scopes = nullScopes },
                CapturePackageFailureKind.MissingOrdinal),
            (
                "scope ordinal outside range",
                WithScope(
                    definition,
                    0,
                    scope => scope with { Ordinal = -1 }),
                CapturePackageFailureKind.MissingOrdinal),
            (
                "duplicate scope ordinal",
                definition with { Scopes = duplicateScopes },
                CapturePackageFailureKind.DuplicateOrdinal),
            (
                "noncanonical scope order",
                definition with { Scopes = reversedScopes },
                CapturePackageFailureKind.NonCanonicalOrder),
            (
                "null request descriptor",
                definition with { Requests = nullRequests },
                CapturePackageFailureKind.MissingOrdinal),
            (
                "request ordinal outside range",
                WithRequest(
                    definition,
                    0,
                    request => request with { Ordinal = -1 }),
                CapturePackageFailureKind.MissingOrdinal),
            (
                "duplicate request ordinal",
                definition with { Requests = duplicateRequests },
                CapturePackageFailureKind.DuplicateOrdinal),
            (
                "noncanonical request order",
                definition with { Requests = reversedRequests },
                CapturePackageFailureKind.NonCanonicalOrder),
            (
                "definition shard count mismatch",
                definition with { ResponseShardCount = 2 },
                CapturePackageFailureKind.AggregateMismatch),
            (
                "definition aggregate mismatch",
                definition with
                {
                    TotalRequestCount =
                        definition.TotalRequestCount + 1,
                },
                CapturePackageFailureKind.AggregateMismatch),
        };

        foreach (var (name, value, expected) in cases)
        {
            AssertCaptureFailure(
                name,
                () => CapturePackageContract.ValidateDefinition(value),
                expected);
        }
    }

    [Fact]
    public void ScopeAndRequestValidationRejectsInconsistentDescriptors()
    {
        var fixture = CreateFixture();
        var definition = fixture.Definition;
        var requests = definition.Requests.ToArray();
        var scopeCases = new[]
        {
            (
                "unsafe scope song ID",
                WithScope(
                    definition,
                    0,
                    scope => scope with { SongId = " " }),
                CapturePackageFailureKind.InvalidMetadata),
            (
                "unknown scope kind",
                WithScope(
                    definition,
                    0,
                    scope => scope with
                    {
                        ScopeKind = (CaptureScopeKind)999,
                    }),
                CapturePackageFailureKind.InvalidMetadata),
            (
                "scope type is not enabled",
                WithScope(
                    definition,
                    0,
                    scope => scope with
                    {
                        LeaderboardType = "Solo_Bass",
                    }),
                CapturePackageFailureKind.MissingScope),
            (
                "unknown scope status",
                WithScope(
                    definition,
                    0,
                    scope => scope with
                    {
                        Status = (CaptureScopeStatus)999,
                    }),
                CapturePackageFailureKind.InvalidMetadata),
            (
                "incomplete scope status",
                WithScope(
                    definition,
                    0,
                    scope => scope with
                    {
                        Status = CaptureScopeStatus.Incomplete,
                    }),
                CapturePackageFailureKind.IncompleteCapture),
            (
                "negative declared scope pages",
                WithScope(
                    definition,
                    0,
                    scope => scope with
                    {
                        DeclaredPageCount = -1,
                    }),
                CapturePackageFailureKind.NegativeCount),
            (
                "negative declared scope entries",
                WithScope(
                    definition,
                    0,
                    scope => scope with
                    {
                        DeclaredEntryCount = -1,
                    }),
                CapturePackageFailureKind.NegativeCount),
            (
                "negative provider scope pages",
                WithScope(
                    definition,
                    0,
                    scope => scope with
                    {
                        ProviderReportedTotalPages = -1,
                    }),
                CapturePackageFailureKind.NegativeCount),
            (
                "negative provider scope entries",
                WithScope(
                    definition,
                    0,
                    scope => scope with
                    {
                        ProviderReportedTotalEntries = -1,
                    }),
                CapturePackageFailureKind.NegativeCount),
            (
                "negative captured scope pages",
                WithScope(
                    definition,
                    0,
                    scope => scope with
                    {
                        CapturedPageCount = -1,
                    }),
                CapturePackageFailureKind.NegativeCount),
            (
                "negative captured scope entries",
                WithScope(
                    definition,
                    0,
                    scope => scope with
                    {
                        CapturedEntryCount = -1,
                    }),
                CapturePackageFailureKind.NegativeCount),
            (
                "negative captured scope requests",
                WithScope(
                    definition,
                    0,
                    scope => scope with
                    {
                        CapturedRequestCount = -1,
                    }),
                CapturePackageFailureKind.NegativeCount),
            (
                "negative captured scope bytes",
                WithScope(
                    definition,
                    0,
                    scope => scope with
                    {
                        CapturedResponseBytes = -1,
                    }),
                CapturePackageFailureKind.NegativeCount),
            (
                "invalid scope content hash",
                WithScope(
                    definition,
                    0,
                    scope => scope with
                    {
                        ContentSha256 = "invalid",
                    }),
                CapturePackageFailureKind.InvalidHash),
            (
                "unsupported scope carries work",
                WithScope(
                    definition,
                    3,
                    scope => scope with
                    {
                        CapturedPageCount = 1,
                    }),
                CapturePackageFailureKind.AggregateMismatch),
            (
                "scope provider totals conflict",
                WithScope(
                    definition,
                    0,
                    scope => scope with
                    {
                        DeclaredPageCount =
                            scope.DeclaredPageCount + 1,
                    }),
                CapturePackageFailureKind.AggregateMismatch),
            (
                "scope zero-universe totals conflict",
                WithScope(
                    definition,
                    2,
                    scope => scope with
                    {
                        DeclaredEntryCount = 1,
                        ProviderReportedTotalEntries = 1,
                    }),
                CapturePackageFailureKind.AggregateMismatch),
            (
                "scope captured totals conflict",
                WithScope(
                    definition,
                    0,
                    scope => scope with
                    {
                        CapturedRequestCount = 0,
                    }),
                CapturePackageFailureKind.AggregateMismatch),
            (
                "scope canonical universe order conflicts",
                WithScope(
                    definition,
                    0,
                    scope => scope with { SongId = "song-b" }),
                CapturePackageFailureKind.NonCanonicalOrder),
            (
                "scope aggregate differs from requests",
                WithScope(
                    definition,
                    0,
                    scope => scope with
                    {
                        CapturedRequestCount =
                            scope.CapturedRequestCount + 1,
                    }),
                CapturePackageFailureKind.AggregateMismatch),
            (
                "scope hash differs from requests",
                WithScope(
                    definition,
                    0,
                    scope => scope with
                    {
                        ContentSha256 = new string('0', 64),
                    }),
                CapturePackageFailureKind.InvalidHash),
        };

        foreach (var (name, value, expected) in scopeCases)
        {
            AssertCaptureFailure(
                name,
                () => CapturePackageContract.ValidateDefinition(value),
                expected);
        }

        var requestCases = new[]
        {
            (
                "unsafe request song ID",
                WithRequest(
                    definition,
                    0,
                    request => request with { SongId = " " }),
                CapturePackageFailureKind.InvalidMetadata),
            (
                "unknown request scope kind",
                WithRequest(
                    definition,
                    0,
                    request => request with
                    {
                        ScopeKind = (CaptureScopeKind)999,
                    }),
                CapturePackageFailureKind.InvalidMetadata),
            (
                "request scope type is not enabled",
                WithRequest(
                    definition,
                    0,
                    request => request with
                    {
                        LeaderboardType = "Solo_Bass",
                    }),
                CapturePackageFailureKind.MissingScope),
            (
                "unknown request status",
                WithRequest(
                    definition,
                    0,
                    request => request with
                    {
                        Status = (CaptureRequestStatus)999,
                    }),
                CapturePackageFailureKind.InvalidMetadata),
            (
                "negative request scope ordinal",
                WithRequest(
                    definition,
                    0,
                    request => request with
                    {
                        ScopeOrdinal = -1,
                    }),
                CapturePackageFailureKind.NegativeCount),
            (
                "negative request scope page ordinal",
                WithRequest(
                    definition,
                    0,
                    request => request with
                    {
                        ScopeRequestOrdinal = -1,
                    }),
                CapturePackageFailureKind.NegativeCount),
            (
                "negative request page",
                WithRequest(
                    definition,
                    0,
                    request => request with
                    {
                        PageIndex = -1,
                    }),
                CapturePackageFailureKind.NegativeCount),
            (
                "request page ordinals differ",
                WithRequest(
                    definition,
                    0,
                    request => request with
                    {
                        ScopeRequestOrdinal = 1,
                    }),
                CapturePackageFailureKind.NonCanonicalOrder),
            (
                "invalid request page size",
                WithRequest(
                    definition,
                    0,
                    request => request with { PageSize = 0 }),
                CapturePackageFailureKind.RecordLimitExceeded),
            (
                "negative request provider pages",
                WithRequest(
                    definition,
                    0,
                    request => request with
                    {
                        ProviderReportedTotalPages = -1,
                    }),
                CapturePackageFailureKind.NegativeCount),
            (
                "negative request provider entries",
                WithRequest(
                    definition,
                    0,
                    request => request with
                    {
                        ProviderReportedTotalEntries = -1,
                    }),
                CapturePackageFailureKind.NegativeCount),
            (
                "request provider pages exceed limit",
                WithRequest(
                    definition,
                    0,
                    request => request with
                    {
                        ProviderReportedTotalPages =
                            CapturePackageFormat
                                .MaximumRequestRecords + 1,
                    }),
                CapturePackageFailureKind.RecordLimitExceeded),
            (
                "negative captured request pages",
                WithRequest(
                    definition,
                    0,
                    request => request with
                    {
                        CapturedPageCount = -1,
                    }),
                CapturePackageFailureKind.NegativeCount),
            (
                "negative captured request entries",
                WithRequest(
                    definition,
                    0,
                    request => request with
                    {
                        CapturedEntryCount = -1,
                    }),
                CapturePackageFailureKind.NegativeCount),
            (
                "negative captured request attempts",
                WithRequest(
                    definition,
                    0,
                    request => request with
                    {
                        CapturedRequestCount = -1,
                    }),
                CapturePackageFailureKind.NegativeCount),
            (
                "negative captured response bytes",
                WithRequest(
                    definition,
                    0,
                    request => request with
                    {
                        CapturedResponseBytes = -1,
                    }),
                CapturePackageFailureKind.NegativeCount),
            (
                "negative response member offset",
                WithRequest(
                    definition,
                    0,
                    request => request with
                    {
                        ResponseOffset = -1,
                    }),
                CapturePackageFailureKind.NegativeCount),
            (
                "invalid response member length",
                WithRequest(
                    definition,
                    0,
                    request => request with
                    {
                        ResponseLength = 0,
                    }),
                CapturePackageFailureKind.RecordLimitExceeded),
            (
                "invalid request content hash",
                WithRequest(
                    definition,
                    0,
                    request => request with
                    {
                        ContentSha256 = "invalid",
                    }),
                CapturePackageFailureKind.InvalidHash),
            (
                "invalid response shard path",
                WithRequest(
                    definition,
                    0,
                    request => request with
                    {
                        ResponseShardPath =
                            "capture/responses/0.jsonl",
                    }),
                CapturePackageFailureKind.InvalidMetadata),
            (
                "incomplete request status",
                WithRequest(
                    definition,
                    0,
                    request => request with
                    {
                        Status = CaptureRequestStatus.Incomplete,
                    }),
                CapturePackageFailureKind.IncompleteCapture),
            (
                "request does not represent one response",
                WithRequest(
                    definition,
                    0,
                    request => request with
                    {
                        CapturedPageCount = 0,
                    }),
                CapturePackageFailureKind.AggregateMismatch),
            (
                "request zero-universe totals conflict",
                WithRequest(
                    definition,
                    2,
                    request => request with
                    {
                        ProviderReportedTotalEntries = 1,
                    }),
                CapturePackageFailureKind.AggregateMismatch),
            (
                "request page exceeds provider total",
                WithRequest(
                    definition,
                    0,
                    request => request with
                    {
                        ScopeRequestOrdinal = 1,
                        PageIndex = 1,
                    }),
                CapturePackageFailureKind.AggregateMismatch),
            (
                "request metadata differs from scope",
                WithRequest(
                    definition,
                    0,
                    request => request with { SongId = "song-b" }),
                CapturePackageFailureKind.AggregateMismatch),
        };

        foreach (var (name, value, expected) in requestCases)
        {
            AssertCaptureFailure(
                name,
                () => CapturePackageContract.ValidateDefinition(value),
                expected);
        }

        var unsupportedAsComplete = WithScope(
            definition,
            3,
            scope => scope with
            {
                CapturedPageCount = 1,
                CapturedRequestCount = 1,
                CapturedResponseBytes = 1,
                Status = CaptureScopeStatus.Complete,
            });
        AssertCaptureFailure(
            "scope status is not proven by catalog support",
            () => CapturePackageContract.ValidateDefinition(
                unsupportedAsComplete),
            CapturePackageFailureKind.IncompleteCapture);

        var extraRequest = requests[0] with
        {
            Ordinal = requests.Length,
            ScopeOrdinal = 99,
            ResponseOffset = 0,
        };
        AssertCaptureFailure(
            "request references a missing scope",
            () => CapturePackageContract.ValidateDefinition(
                definition with
                {
                    Requests = [.. requests, extraRequest],
                }),
            CapturePackageFailureKind.MissingScope);

        AssertCaptureFailure(
            "scope universe must be complete",
            () => CapturePackageContract.ValidateDefinition(
                definition with
                {
                    TotalScopeCount =
                        definition.TotalScopeCount - 1,
                    Scopes = definition.Scopes
                        .Take(definition.Scopes.Count - 1)
                        .ToArray(),
                }),
            CapturePackageFailureKind.MissingScope);

        var twoPageDefinition = CreateScaleDefinition(
            scopeCount: 1,
            pagesPerScope: 2,
            responsesPerShard: 2,
            responseLength: 256);
        AssertCaptureFailure(
            "scope request ordinals must remain contiguous",
            () => CapturePackageContract.ValidateDefinition(
                WithRequest(
                    twoPageDefinition,
                    1,
                    request => request with
                    {
                        ScopeRequestOrdinal = 0,
                        PageIndex = 0,
                    })),
            CapturePackageFailureKind.MissingOrdinal);
        AssertCaptureFailure(
            "scope request count must match provider pages",
            () => CapturePackageContract.ValidateDefinition(
                twoPageDefinition with
                {
                    Requests =
                    [
                        twoPageDefinition.Requests[0],
                    ],
                }),
            CapturePackageFailureKind.MissingOrdinal);

        AssertCaptureFailure(
            "response shard ordinal starts with a gap",
            () => CapturePackageContract.BuildResponseShardLayouts(
                requests.Select(request => request with
                {
                    ResponseShardPath =
                        CapturePackageFormat.ResponseShardPath(1),
                }).ToArray()),
            CapturePackageFailureKind.MissingOrdinal);
        AssertCaptureFailure(
            "response shard exceeds its byte limit",
            () => CapturePackageContract.BuildResponseShardLayouts(
            [
                requests[0] with
                {
                    ResponseLength = checked((int)
                        CapturePackageFormat
                            .MaximumResponseShardBytes + 1),
                },
            ]),
            CapturePackageFailureKind.RecordLimitExceeded);
        AssertCaptureFailure(
            "response shard layout requires at least one shard",
            () => CapturePackageContract.BuildResponseShardLayouts(
                Array.Empty<CaptureRequestDescriptor>()),
            CapturePackageFailureKind.RecordLimitExceeded);
        AssertCaptureFailure(
            "scope fingerprint rejects invalid shard paths",
            () => CapturePackageContract.ComputeScopeContentSha256(
            [
                requests[0] with
                {
                    ResponseShardPath = "capture/responses/0.jsonl",
                },
            ]),
            CapturePackageFailureKind.InvalidMetadata);
        AssertCaptureFailure(
            "scope fingerprint rejects invalid content hashes",
            () => CapturePackageContract.ComputeScopeContentSha256(
            [
                requests[0] with { ContentSha256 = "invalid" },
            ]),
            CapturePackageFailureKind.InvalidHash);
        AssertCaptureFailure(
            "scope fingerprint rejects unknown leaderboard types",
            () => CapturePackageContract.ComputeScopeContentSha256(
            [
                requests[0] with
                {
                    LeaderboardType = "Solo_Unknown",
                },
            ]),
            CapturePackageFailureKind.InvalidMetadata);
    }

    [Fact]
    public void ManifestValidationRejectsInvalidMetadataAndReferences()
    {
        var fixture = CreateFixture();
        var manifest = CreateManifest(fixture);
        var cases = new[]
        {
            (
                "missing manifest source",
                manifest with { Source = null! },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "unsupported manifest format",
                manifest with { FormatId = "unsupported" },
                CapturePackageFailureKind.UnsupportedFormat),
            (
                "unsafe manifest capture ID",
                manifest with { CaptureId = " " },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "invalid envelope attempt",
                manifest with { EnvelopeAttempt = 0 },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "unknown manifest status",
                manifest with
                {
                    Status = (CapturePackageStatus)999,
                },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "incomplete manifest status",
                manifest with
                {
                    Status = CapturePackageStatus.Incomplete,
                },
                CapturePackageFailureKind.IncompleteCapture),
            (
                "unsupported catalog identity",
                manifest with
                {
                    Catalog = manifest.Catalog with { Version = 0 },
                },
                CapturePackageFailureKind.UnsupportedFormat),
            (
                "invalid catalog identity bytes",
                manifest with
                {
                    Catalog = manifest.Catalog with { Bytes = 0 },
                },
                CapturePackageFailureKind.RecordLimitExceeded),
            (
                "invalid catalog identity hash",
                manifest with
                {
                    Catalog = manifest.Catalog with
                    {
                        ContentSha256 = "invalid",
                    },
                },
                CapturePackageFailureKind.InvalidHash),
            (
                "catalog identity differs from envelope",
                manifest with
                {
                    Catalog = manifest.Catalog with
                    {
                        ContentSha256 = new string('0', 64),
                    },
                },
                CapturePackageFailureKind.EnvelopeMismatch),
            (
                "null manifest enabled type",
                manifest with
                {
                    EnabledSoloInstruments = [null!],
                },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "duplicate manifest enabled type",
                manifest with
                {
                    EnabledSoloInstruments =
                    [
                        "Solo_Guitar",
                        "Solo_Guitar",
                    ],
                },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "noncanonical manifest enabled order",
                manifest with
                {
                    EnabledSoloInstruments =
                    [
                        "Solo_Bass",
                        "Solo_Guitar",
                    ],
                },
                CapturePackageFailureKind.NonCanonicalOrder),
            (
                "manifest has no enabled types",
                manifest with
                {
                    EnabledSoloInstruments = [],
                    EnabledBandTypes = [],
                },
                CapturePackageFailureKind.MissingScope),
            (
                "missing manifest capture start",
                manifest with
                {
                    CaptureStartedAtUtc = default,
                },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "manifest capture starts before envelope",
                manifest with
                {
                    CaptureStartedAtUtc =
                        manifest.EnvelopeCreatedAtUtc.AddTicks(-1),
                },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "invalid manifest scope count",
                manifest with { TotalScopeCount = 0 },
                CapturePackageFailureKind.RecordLimitExceeded),
            (
                "negative manifest entry count",
                manifest with { TotalEntryCount = -1 },
                CapturePackageFailureKind.NegativeCount),
            (
                "zero manifest page count",
                manifest with { TotalPageCount = 0 },
                CapturePackageFailureKind.IncompleteCapture),
            (
                "manifest page count exceeds limit",
                manifest with
                {
                    TotalPageCount =
                        CapturePackageFormat
                            .MaximumRequestRecords + 1,
                },
                CapturePackageFailureKind.RecordLimitExceeded),
            (
                "manifest shard count exceeds limit",
                manifest with
                {
                    ResponseShardCount =
                        CapturePackageFormat
                            .MaximumResponseShards + 1,
                },
                CapturePackageFailureKind.RecordLimitExceeded),
            (
                "invalid request-plan reference identity",
                manifest with
                {
                    RequestPlan = manifest.RequestPlan with
                    {
                        Path = "capture/other.jsonl",
                    },
                },
                CapturePackageFailureKind.InvalidMetadata),
            (
                "invalid request-plan reference bounds",
                manifest with
                {
                    RequestPlan = manifest.RequestPlan with
                    {
                        Bytes = 0,
                    },
                },
                CapturePackageFailureKind.RecordLimitExceeded),
            (
                "invalid request-plan reference hash",
                manifest with
                {
                    RequestPlan = manifest.RequestPlan with
                    {
                        Sha256 = "invalid",
                    },
                },
                CapturePackageFailureKind.InvalidHash),
            (
                "invalid manifest root hash",
                manifest with { ManifestRootHash = "invalid" },
                CapturePackageFailureKind.InvalidHash),
            (
                "stale manifest root hash",
                manifest with
                {
                    ManifestRootHash = new string('0', 64),
                },
                CapturePackageFailureKind.InvalidHash),
        };

        foreach (var (name, value, expected) in cases)
        {
            AssertCaptureFailure(
                name,
                () => CapturePackageContract.SerializeManifest(value),
                expected);
        }

        AssertCaptureFailure(
            "validated manifest requires a root hash",
            () => CapturePackageContract.ValidateManifest(
                manifest with { ManifestRootHash = null }),
            CapturePackageFailureKind.InvalidHash);
        AssertCaptureFailure(
            "validated manifest rejects a stale root hash",
            () => CapturePackageContract.ValidateManifest(
                manifest with
                {
                    ManifestRootHash = new string('0', 64),
                }),
            CapturePackageFailureKind.InvalidHash);
        AssertCaptureFailure(
            "manifest catalog identity requires canonical bytes",
            () => CapturePackageContract.CreateCatalogIdentity(
                fixture.Definition.Catalog,
                Encoding.UTF8.GetBytes("{}")),
            CapturePackageFailureKind.NonCanonicalJson);
        AssertCaptureFailure(
            "manifest creation rejects a mismatched catalog identity",
            () => CapturePackageContract.CreateManifest(
                fixture.Envelope,
                fixture.Definition,
                manifest.Catalog with
                {
                    ContentSha256 = new string('0', 64),
                },
                manifest.RequestPlan,
                manifest.ScopeCompleteness),
            CapturePackageFailureKind.ArtifactMismatch);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CapturePackageContract.ComputeScopeContentSha256(
                fixture.Definition.Requests,
                -1,
                1));
        AssertCaptureFailure(
            "scope fingerprint rejects null requests",
            () => CapturePackageContract.ComputeScopeContentSha256(
                new CaptureRequestDescriptor[] { null! }),
            CapturePackageFailureKind.InvalidMetadata);

        var canonical =
            CapturePackageContract.SerializeManifest(manifest);
        var parserCases = new[]
        {
            (
                "empty manifest bytes",
                Array.Empty<byte>(),
                CapturePackageFailureKind.RecordLimitExceeded),
            (
                "invalid manifest JSON",
                Encoding.UTF8.GetBytes("{"),
                CapturePackageFailureKind.InvalidMetadata),
            (
                "noncanonical manifest JSON",
                [.. canonical, (byte)' '],
                CapturePackageFailureKind.NonCanonicalJson),
        };
        foreach (var (name, bytes, expected) in parserCases)
        {
            AssertCaptureFailure(
                name,
                () => CapturePackageContract.DeserializeManifest(bytes),
                expected);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CapturePackageFormat.ResponseShardPath(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CapturePackageFormat.ResponseShardPath(
                CapturePackageFormat.MaximumResponseShards));
    }

    [Fact]
    public async Task JsonLinesParserRejectsInvalidBoundsAndSyntax()
    {
        AssertCaptureFailure(
            "JSONL measurement requires records",
            () => CapturePackageJsonLines.Measure(
                Array.Empty<JsonElement>(),
                1,
                10,
                10,
                "test JSONL"),
            CapturePackageFailureKind.RecordLimitExceeded);
        AssertCaptureFailure(
            "JSONL measurement rejects null records",
            () => CapturePackageJsonLines.Measure(
                new JsonElement?[] { null! },
                1,
                10,
                10,
                "test JSONL"),
            CapturePackageFailureKind.InvalidMetadata);
        AssertCaptureFailure(
            "JSONL measurement rejects oversized records",
            () => CapturePackageJsonLines.Measure(
                new[] { Entry("""{"value":"long"}""") },
                1,
                100,
                2,
                "test JSONL"),
            CapturePackageFailureKind.RecordLimitExceeded);
        AssertCaptureFailure(
            "JSONL measurement rejects oversized sets",
            () => CapturePackageJsonLines.Measure(
                new[] { Entry("{}") },
                1,
                1,
                100,
                "test JSONL"),
            CapturePackageFailureKind.RecordLimitExceeded);

        var invalidCases = new[]
        {
            new JsonLinesFailureCase(
                "invalid expected record count",
                Encoding.UTF8.GetBytes("{}\n"),
                ExpectedBytes: 3,
                ExpectedCount: 0,
                MaximumRecords: 1,
                MaximumBytes: 10,
                MaximumRecordBytes: 10,
                CapturePackageFailureKind.RecordLimitExceeded),
            new JsonLinesFailureCase(
                "invalid declared byte count",
                Encoding.UTF8.GetBytes("{}\n"),
                ExpectedBytes: 0,
                ExpectedCount: 1,
                MaximumRecords: 1,
                MaximumBytes: 10,
                MaximumRecordBytes: 10,
                CapturePackageFailureKind.RecordLimitExceeded),
            new JsonLinesFailureCase(
                "record count cannot fit declared bytes",
                Encoding.UTF8.GetBytes("{}\n"),
                ExpectedBytes: 3,
                ExpectedCount: 2,
                MaximumRecords: 2,
                MaximumBytes: 10,
                MaximumRecordBytes: 10,
                CapturePackageFailureKind.RecordLimitExceeded),
            new JsonLinesFailureCase(
                "content exceeds declared bytes",
                Encoding.UTF8.GetBytes("{}\n"),
                ExpectedBytes: 2,
                ExpectedCount: 1,
                MaximumRecords: 1,
                MaximumBytes: 10,
                MaximumRecordBytes: 10,
                CapturePackageFailureKind.RecordLimitExceeded),
            new JsonLinesFailureCase(
                "record exceeds byte limit",
                Encoding.UTF8.GetBytes("{}\n"),
                ExpectedBytes: 3,
                ExpectedCount: 1,
                MaximumRecords: 1,
                MaximumBytes: 10,
                MaximumRecordBytes: 1,
                CapturePackageFailureKind.RecordLimitExceeded),
            new JsonLinesFailureCase(
                "blank record",
                Encoding.UTF8.GetBytes("\n"),
                ExpectedBytes: 3,
                ExpectedCount: 1,
                MaximumRecords: 1,
                MaximumBytes: 10,
                MaximumRecordBytes: 10,
                CapturePackageFailureKind.NonCanonicalJson),
            new JsonLinesFailureCase(
                "more records than declared",
                Encoding.UTF8.GetBytes("{}\n{}\n"),
                ExpectedBytes: 6,
                ExpectedCount: 1,
                MaximumRecords: 2,
                MaximumBytes: 10,
                MaximumRecordBytes: 10,
                CapturePackageFailureKind.AggregateMismatch),
            new JsonLinesFailureCase(
                "invalid JSON record",
                Encoding.UTF8.GetBytes("{]\n"),
                ExpectedBytes: 3,
                ExpectedCount: 1,
                MaximumRecords: 1,
                MaximumBytes: 10,
                MaximumRecordBytes: 10,
                CapturePackageFailureKind.InvalidMetadata),
            new JsonLinesFailureCase(
                "noncanonical JSON record",
                Encoding.UTF8.GetBytes("{ }\n"),
                ExpectedBytes: 4,
                ExpectedCount: 1,
                MaximumRecords: 1,
                MaximumBytes: 10,
                MaximumRecordBytes: 10,
                CapturePackageFailureKind.NonCanonicalJson),
            new JsonLinesFailureCase(
                "missing final newline",
                Encoding.UTF8.GetBytes("{}"),
                ExpectedBytes: 3,
                ExpectedCount: 1,
                MaximumRecords: 1,
                MaximumBytes: 10,
                MaximumRecordBytes: 10,
                CapturePackageFailureKind.NonCanonicalJson),
            new JsonLinesFailureCase(
                "record total differs from descriptor",
                Encoding.UTF8.GetBytes("{\"a\":1}\n"),
                ExpectedBytes: 8,
                ExpectedCount: 2,
                MaximumRecords: 2,
                MaximumBytes: 10,
                MaximumRecordBytes: 10,
                CapturePackageFailureKind.AggregateMismatch),
            new JsonLinesFailureCase(
                "byte total differs from descriptor",
                Encoding.UTF8.GetBytes("{}\n"),
                ExpectedBytes: 4,
                ExpectedCount: 1,
                MaximumRecords: 1,
                MaximumBytes: 10,
                MaximumRecordBytes: 10,
                CapturePackageFailureKind.AggregateMismatch),
        };

        foreach (var testCase in invalidCases)
        {
            await AssertCaptureFailureAsync(
                testCase.Name,
                async () =>
                {
                    await using var stream = new MemoryStream(
                        testCase.Bytes,
                        writable: false);
                    _ = await CapturePackageJsonLines
                        .ReadAsync<JsonElement>(
                            stream,
                            testCase.ExpectedBytes,
                            testCase.ExpectedCount,
                            testCase.MaximumRecords,
                            testCase.MaximumBytes,
                            testCase.MaximumRecordBytes,
                            "test JSONL",
                            preDeserialize: null,
                            static (_, _, _, _, _) => { },
                            CancellationToken.None);
                },
                testCase.ExpectedFailure);
        }

        var unreadable = new MemoryStream();
        unreadable.Dispose();
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await CapturePackageJsonLines
                .ReadAsync<JsonElement>(
                    unreadable,
                    3,
                    1,
                    1,
                    10,
                    10,
                    "test JSONL",
                    preDeserialize: null,
                    static (_, _, _, _, _) => { },
                    CancellationToken.None));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () =>
            {
                await using var stream = new MemoryStream(
                    Encoding.UTF8.GetBytes("{}\n"),
                    writable: false);
                _ = await CapturePackageJsonLines
                    .ReadAsync<JsonElement>(
                        stream,
                        3,
                        1,
                        1,
                        10,
                        10,
                        "test JSONL",
                        preDeserialize: null,
                        static (_, _, _, _, _) => { },
                        cancellation.Token);
            });
    }

    [Fact]
    public async Task CanonicalJsonLinesStreamHonorsStreamContract()
    {
        await using var stream =
            CapturePackageJsonLines.OpenReadStream(
                new[] { Entry("{}"), Entry("""{"rank":1}""") },
                maximumRecordBytes: 100,
                "test JSONL");
        Assert.True(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => stream.Length);
        Assert.Throws<NotSupportedException>(() => stream.Position);
        Assert.Throws<NotSupportedException>(() =>
            stream.Position = 0);
        Assert.Throws<NotSupportedException>(() =>
            stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() =>
            stream.SetLength(0));
        Assert.Throws<NotSupportedException>(() =>
            stream.Write([], 0, 0));
        stream.Flush();
        Assert.Equal(0, stream.Read([], 0, 0));

        var output = new MemoryStream();
        var buffer = new byte[3];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            output.Write(buffer, 0, read);
        Assert.Equal("{}\n{\"rank\":1}\n", Encoding.UTF8.GetString(
            output.ToArray()));

        await using var canceledStream =
            CapturePackageJsonLines.OpenReadStream(
                new[] { Entry("{}") },
                maximumRecordBytes: 100,
                "test JSONL");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await canceledStream.ReadExactlyAsync(
                new byte[1],
                cancellation.Token));

        await using var nullRowStream =
            CapturePackageJsonLines.OpenReadStream(
                new JsonElement?[] { null! },
                maximumRecordBytes: 100,
                "test JSONL");
        AssertCaptureFailure(
            "canonical JSONL stream rejects null records",
            () => nullRowStream.ReadByte(),
            CapturePackageFailureKind.InvalidMetadata);

        await using var oversizedStream =
            CapturePackageJsonLines.OpenReadStream(
                new[] { Entry("""{"value":"long"}""") },
                maximumRecordBytes: 2,
                "test JSONL");
        AssertCaptureFailure(
            "canonical JSONL stream rejects oversized records",
            () => oversizedStream.ReadByte(),
            CapturePackageFailureKind.RecordLimitExceeded);
    }

    [Fact]
    public void StorageAdmissionRejectsInvalidArgumentsAndAllConstraints()
    {
        var policy = new CapturePackageStoragePolicy(
            MaximumPackageBytes: 100,
            MinimumFreeSpaceReserveBytes: 10,
            MaximumRetainedSealedPackages: 2);
        var state = new CapturePackageStorageState(
            ProposedPackageBytes: 50,
            AvailableFreeSpaceBytes: 100,
            RetainedSealedPackageCount: 0);

        Assert.Throws<ArgumentNullException>(() =>
            CapturePackageStorageAdmission.Evaluate(null!, state));
        Assert.Throws<ArgumentNullException>(() =>
            CapturePackageStorageAdmission.Evaluate(policy, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CapturePackageStorageAdmission.Evaluate(
                policy with { MaximumPackageBytes = 0 },
                state));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CapturePackageStorageAdmission.Evaluate(
                policy with
                {
                    MinimumFreeSpaceReserveBytes = -1,
                },
                state));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CapturePackageStorageAdmission.Evaluate(
                policy with
                {
                    MaximumRetainedSealedPackages = 0,
                },
                state));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CapturePackageStorageAdmission.Evaluate(
                policy,
                state with { ProposedPackageBytes = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CapturePackageStorageAdmission.Evaluate(
                policy,
                state with { AvailableFreeSpaceBytes = -1 }));

        var rejected = CapturePackageStorageAdmission.Evaluate(
            policy,
            new CapturePackageStorageState(
                ProposedPackageBytes: 101,
                AvailableFreeSpaceBytes: 50,
                RetainedSealedPackageCount: 2));
        Assert.Equal(
            [
                CapturePackageAdmissionRejection.PackageTooLarge,
                CapturePackageAdmissionRejection
                    .InsufficientFreeSpace,
                CapturePackageAdmissionRejection
                    .RetentionLimitReached,
            ],
            rejected.Rejections);
        Assert.Null(
            rejected.ProjectedRemainingFreeSpaceBytesIfAdmitted);

        var saturated = CapturePackageStorageAdmission.Evaluate(
            policy with
            {
                MaximumRetainedSealedPackages = int.MaxValue,
            },
            state with
            {
                RetainedSealedPackageCount = int.MaxValue,
            });
        Assert.Null(
            saturated.ProjectedRetainedSealedPackageCountIfAdmitted);
    }

    [Fact]
    public async Task ReaderWriterAndArtifactHelpersRejectExceptionalInputs()
    {
        using var directory =
            new PackageDirectory("capture-exceptional-inputs");
        var fixture = CreateFixture();
        var writer = await CapturePackageWriter.CreateAsync(
            directory.Path,
            fixture.Envelope);
        Assert.Equal(
            Path.GetFullPath(directory.Path),
            writer.RootPath);
        Assert.False(writer.IsSealed);
        Assert.Empty(writer.Artifacts);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => writer.AddArtifactAsync(
                null!,
                new byte[] { 1 }));
        await AssertCaptureFailureAsync(
            "capture writer owns metadata paths",
            () => writer.AddArtifactAsync(
                new TierZeroArtifactRegistration(
                    CapturePackageFormat.ManifestOwner,
                    CapturePackageFormat.ManifestPath,
                    CapturePackageFormat.JsonMediaType,
                    CapturePackageFormat.DescriptorSchemaVersion,
                    1,
                    1),
                new byte[] { 1 }),
            CapturePackageFailureKind.ArtifactMismatch);
        await AssertCaptureFailureAsync(
            "capture writer rejects unknown content paths",
            () => writer.AddArtifactAsync(
                new TierZeroArtifactRegistration(
                    "other",
                    "capture/other.bin",
                    "application/octet-stream",
                    1,
                    1,
                    1),
                new byte[] { 1 }),
            CapturePackageFailureKind.ArtifactMismatch);
        await AssertCaptureFailureAsync(
            "capture writer rejects empty content",
            () => writer.AddArtifactAsync(
                ResponseRegistration(
                    CapturePackageFormat.ResponseShardPath(0),
                    1,
                    1),
                ReadOnlyMemory<byte>.Empty),
            CapturePackageFailureKind.ArtifactMismatch);
        await AssertCaptureFailureAsync(
            "capture writer rejects oversized catalog content",
            () => writer.AddArtifactAsync(
                new TierZeroArtifactRegistration(
                    CapturePackageFormat.CatalogOwner,
                    CapturePackageFormat.CatalogPath,
                    CapturePackageFormat.JsonMediaType,
                    CapturePackageFormat.CatalogSchemaVersion,
                    fixture.Definition.Catalog.SongCount,
                    CapturePackageFormat.MaximumCatalogBytes + 1),
                new byte[
                    checked((int)
                        CapturePackageFormat.MaximumCatalogBytes + 1)]),
            CapturePackageFailureKind.RecordLimitExceeded);
        await AssertCaptureFailureAsync(
            "capture writer rejects seal timestamps before completion",
            () => writer.SealAsync(
                fixture.Definition,
                fixture.Definition.CaptureCompletedAtUtc
                    .AddTicks(-1)),
            CapturePackageFailureKind.InvalidMetadata);

        await AssertCaptureFailureAsync(
            "content write requires canonical paths",
            () => CapturePackageContract
                .ValidateContentArtifactForWriteAsync(
                    CatalogRegistration(fixture) with
                    {
                        Path = "capture\\catalog.json",
                    },
                    fixture.CatalogBytes,
                    fixture.Envelope,
                    CancellationToken.None),
            CapturePackageFailureKind.InvalidMetadata);
        await AssertCaptureFailureAsync(
            "content write rejects empty bytes",
            () => CapturePackageContract
                .ValidateContentArtifactForWriteAsync(
                    CatalogRegistration(fixture),
                    [],
                    fixture.Envelope,
                    CancellationToken.None),
            CapturePackageFailureKind.ArtifactMismatch);
        await AssertCaptureFailureAsync(
            "content write rejects unknown paths",
            () => CapturePackageContract
                .ValidateContentArtifactForWriteAsync(
                    new TierZeroArtifactRegistration(
                        "other",
                        "capture/other.json",
                        CapturePackageFormat.JsonMediaType,
                        1,
                        1,
                        2),
                    Encoding.UTF8.GetBytes("{}"),
                    fixture.Envelope,
                    CancellationToken.None),
            CapturePackageFailureKind.ArtifactMismatch);
        await AssertCaptureFailureAsync(
            "response shard registration requires rows",
            () => CapturePackageContract
                .ValidateContentArtifactForWriteAsync(
                    ResponseRegistration(
                        CapturePackageFormat.ResponseShardPath(0),
                        0,
                        fixture.ResponseShardBytes.Length),
                    fixture.ResponseShardBytes,
                    fixture.Envelope,
                    CancellationToken.None),
            CapturePackageFailureKind.RecordLimitExceeded);
        await AssertCaptureFailureAsync(
            "response shard registration identity is exact",
            () => CapturePackageContract
                .ValidateContentArtifactForWriteAsync(
                    ResponseRegistration(
                        CapturePackageFormat.ResponseShardPath(0),
                        fixture.Responses.Count,
                        fixture.ResponseShardBytes.Length) with
                    {
                        LogicalOwner = "wrong-owner",
                    },
                    fixture.ResponseShardBytes,
                    fixture.Envelope,
                    CancellationToken.None),
            CapturePackageFailureKind.ArtifactMismatch);
        await AssertCaptureFailureAsync(
            "catalog must match envelope identity",
            () => CapturePackageContract
                .ValidateContentArtifactForWriteAsync(
                    CatalogRegistration(fixture),
                    fixture.CatalogBytes,
                    fixture.Envelope with
                    {
                        Source = fixture.Envelope.Source with
                        {
                            Catalog =
                                fixture.Envelope.Source.Catalog with
                                {
                                    ContentSha256 =
                                        new string('0', 64),
                                },
                        },
                    },
                    CancellationToken.None),
            CapturePackageFailureKind.EnvelopeMismatch);

        var metadataDescriptor =
            new TierZeroArtifactDescriptor(
                CapturePackageFormat.RequestPlanOwner,
                CapturePackageFormat.RequestPlanPath,
                CapturePackageFormat.JsonLinesMediaType,
                CapturePackageFormat.DescriptorSchemaVersion,
                1,
                [],
                1,
                1,
                Hash("metadata"));
        AssertCaptureFailure(
            "content changes stop after metadata starts",
            () => CapturePackageContract
                .EnsureContentAdditionAllowed([metadataDescriptor]),
            CapturePackageFailureKind.ArtifactMismatch);

        var byPath =
            new Dictionary<string, TierZeroArtifactDescriptor>(
                StringComparer.Ordinal);
        AssertCaptureFailure(
            "required artifact is missing",
            () => CapturePackageContract.RequireArtifact(
                byPath,
                CapturePackageFormat.CatalogPath),
            CapturePackageFailureKind.ArtifactMismatch);
        byPath[metadataDescriptor.Path] = metadataDescriptor;
        AssertCaptureFailure(
            "required artifact descriptor differs",
            () => CapturePackageContract.RequireArtifact(
                byPath,
                metadataDescriptor with
                {
                    LogicalOwner = "different-owner",
                }),
            CapturePackageFailureKind.ArtifactMismatch);

        Directory.CreateDirectory(directory.Path);
        var artifactPath = Path.Combine(
            directory.Path,
            "artifact.json");
        var artifactBytes = Encoding.UTF8.GetBytes("{}");
        await File.WriteAllBytesAsync(artifactPath, artifactBytes);
        var artifact = new TierZeroArtifactDescriptor(
            "test",
            "artifact.json",
            CapturePackageFormat.JsonMediaType,
            1,
            1,
            [],
            artifactBytes.Length,
            artifactBytes.Length,
            Hash(artifactBytes));
        await AssertCaptureFailureAsync(
            "artifact reader enforces descriptor bounds",
            () => CapturePackageContract.ReadArtifactBytesAsync(
                directory.Path,
                artifact with
                {
                    CompressedBytes = 0,
                    UncompressedBytes = 0,
                },
                100,
                CancellationToken.None),
            CapturePackageFailureKind.ArtifactMismatch);
        await AssertCaptureFailureAsync(
            "artifact reader requires canonical paths",
            () => CapturePackageContract.ReadArtifactBytesAsync(
                directory.Path,
                artifact with { Path = "folder\\artifact.json" },
                100,
                CancellationToken.None),
            CapturePackageFailureKind.InvalidMetadata);
        await AssertCaptureFailureAsync(
            "artifact reader detects preflight identity changes",
            () => CapturePackageContract.ReadArtifactBytesAsync(
                directory.Path,
                artifact,
                100,
                CancellationToken.None,
                new TierZeroFileSnapshot(
                    artifactBytes.Length,
                    "different")),
            CapturePackageFailureKind.ArtifactMismatch);
        await AssertCaptureFailureAsync(
            "artifact reader detects size changes",
            () => CapturePackageContract.ReadArtifactBytesAsync(
                directory.Path,
                artifact with
                {
                    CompressedBytes =
                        artifact.CompressedBytes + 1,
                    UncompressedBytes =
                        artifact.UncompressedBytes + 1,
                },
                100,
                CancellationToken.None),
            CapturePackageFailureKind.ArtifactMismatch);
        await AssertCaptureFailureAsync(
            "artifact reader detects hash changes",
            () => CapturePackageContract.ReadArtifactBytesAsync(
                directory.Path,
                artifact with { Sha256 = new string('0', 64) },
                100,
                CancellationToken.None),
            CapturePackageFailureKind.ArtifactMismatch);

        var jsonLinesPath = Path.Combine(
            directory.Path,
            "records.jsonl");
        var jsonLinesBytes = Encoding.UTF8.GetBytes("{}\n");
        await File.WriteAllBytesAsync(
            jsonLinesPath,
            jsonLinesBytes);
        var jsonLinesArtifact = new TierZeroArtifactDescriptor(
            "test",
            "records.jsonl",
            CapturePackageFormat.JsonLinesMediaType,
            1,
            1,
            [],
            jsonLinesBytes.Length,
            jsonLinesBytes.Length,
            Hash(jsonLinesBytes));
        Task<CaptureJsonLinesMeasurement> ReadJsonLines(
            TierZeroArtifactDescriptor descriptor,
            int expectedCount = 1,
            long expectedBytes = 3,
            TierZeroFileSnapshot? expectedSnapshot = null) =>
            CapturePackageContract.ReadJsonLinesArtifactAsync<
                JsonElement>(
                directory.Path,
                descriptor,
                expectedCount,
                expectedBytes,
                maximumRecords: 2,
                maximumBytes: 100,
                maximumRecordBytes: 100,
                "test JSONL artifact",
                preDeserialize: null,
                static (_, _, _, _, _) => { },
                CancellationToken.None,
                expectedSnapshot);

        await AssertCaptureFailureAsync(
            "JSONL artifact descriptor must match its reference",
            () => ReadJsonLines(
                jsonLinesArtifact,
                expectedCount: 2),
            CapturePackageFailureKind.ArtifactMismatch);
        await AssertCaptureFailureAsync(
            "JSONL artifact detects preflight identity changes",
            () => ReadJsonLines(
                jsonLinesArtifact,
                expectedSnapshot: new TierZeroFileSnapshot(
                    jsonLinesBytes.Length,
                    "different")),
            CapturePackageFailureKind.ArtifactMismatch);
        await AssertCaptureFailureAsync(
            "JSONL artifact detects size changes",
            () => ReadJsonLines(
                jsonLinesArtifact with
                {
                    CompressedBytes =
                        jsonLinesArtifact.CompressedBytes + 1,
                    UncompressedBytes =
                        jsonLinesArtifact.UncompressedBytes + 1,
                },
                expectedBytes: 4),
            CapturePackageFailureKind.ArtifactMismatch);
        await AssertCaptureFailureAsync(
            "JSONL artifact detects hash changes",
            () => ReadJsonLines(
                jsonLinesArtifact with
                {
                    Sha256 = new string('0', 64),
                }),
            CapturePackageFailureKind.ArtifactMismatch);

        await writer.MarkInterruptedAsync(
            "coverage interruption",
            CreatedAt.AddMinutes(8));
        Assert.False(writer.IsSealed);

        using var mismatchedEnvelopeDirectory =
            new PackageDirectory("capture-envelope-mismatch");
        var mismatchedEnvelopeWriter =
            await CapturePackageWriter.CreateAsync(
                mismatchedEnvelopeDirectory.Path,
                fixture.Envelope with
                {
                    Source = fixture.Envelope.Source with
                    {
                        Catalog =
                            fixture.Envelope.Source.Catalog with
                            {
                                ContentSha256 =
                                    new string('0', 64),
                            },
                    },
                });
        await AssertCaptureFailureAsync(
            "capture seal rechecks the envelope catalog identity",
            () => mismatchedEnvelopeWriter.SealAsync(
                fixture.Definition,
                CreatedAt.AddMinutes(7)),
            CapturePackageFailureKind.EnvelopeMismatch);

        using var sealedDirectory =
            new PackageDirectory("capture-descriptor-failures");
        var sealedWriter = await CapturePackageWriter.CreateAsync(
            sealedDirectory.Path,
            fixture.Envelope);
        await AddContentArtifactsAsync(sealedWriter, fixture);
        var sealedPackage = await sealedWriter.SealAsync(
            fixture.Definition,
            CreatedAt.AddMinutes(7));
        Assert.True(sealedWriter.IsSealed);
        var catalogIdentity =
            CapturePackageContract.CreateCatalogIdentity(
                fixture.Definition.Catalog,
                fixture.CatalogBytes);
        var artifacts = sealedPackage.Envelope.Artifacts.ToArray();
        var responseIndex = Array.FindIndex(
            artifacts,
            static candidate =>
                candidate.LogicalOwner ==
                CapturePackageFormat.ResponseShardOwner);

        var invalidResponseDescriptors = artifacts.ToArray();
        invalidResponseDescriptors[responseIndex] =
            invalidResponseDescriptors[responseIndex] with
            {
                LogicalOwner = "wrong-owner",
            };
        AssertCaptureFailure(
            "response descriptor identity is exact",
            () => CapturePackageContract
                .ValidateContentArtifactDescriptors(
                    fixture.Definition,
                    catalogIdentity,
                    invalidResponseDescriptors,
                    requireMetadata: true),
            CapturePackageFailureKind.ArtifactMismatch);
        AssertCaptureFailure(
            "artifact descriptor paths are unique",
            () => CapturePackageContract
                .ValidateContentArtifactDescriptors(
                    fixture.Definition,
                    catalogIdentity,
                    [.. artifacts, artifacts[0]],
                    requireMetadata: true),
            CapturePackageFailureKind.ArtifactMismatch);

        var extraArtifact = new TierZeroArtifactDescriptor(
            "extra",
            "capture/extra.json",
            CapturePackageFormat.JsonMediaType,
            1,
            1,
            [],
            1,
            1,
            Hash("extra"));
        AssertCaptureFailure(
            "capture descriptor set is closed",
            () => CapturePackageContract
                .ValidateContentArtifactDescriptors(
                    fixture.Definition,
                    catalogIdentity,
                    [.. artifacts, extraArtifact],
                    requireMetadata: true),
            CapturePackageFailureKind.ArtifactMismatch);

        var invalidResponseRowCount = artifacts.ToArray();
        invalidResponseRowCount[responseIndex] =
            invalidResponseRowCount[responseIndex] with
            {
                RowCount = 0,
            };
        AssertCaptureFailure(
            "response descriptor row count is bounded",
            () => CapturePackageContract.PreflightEnvelopeArtifacts(
                sealedPackage.Manifest,
                invalidResponseRowCount),
            CapturePackageFailureKind.RecordLimitExceeded);
        AssertCaptureFailure(
            "preflight rejects extra artifact descriptors",
            () => CapturePackageContract.PreflightEnvelopeArtifacts(
                sealedPackage.Manifest,
                [.. artifacts, extraArtifact]),
            CapturePackageFailureKind.ArtifactMismatch);

        AssertCaptureFailure(
            "invalid Tier-0 envelope metadata is translated",
            () => CapturePackageContract.ValidateEnvelope(
                sealedPackage.Manifest,
                sealedPackage.Envelope with { Attempt = 0 }),
            CapturePackageFailureKind.EnvelopeMismatch);
        AssertCaptureFailure(
            "capture and Tier-0 envelope identities must match",
            () => CapturePackageContract.ValidateEnvelope(
                sealedPackage.Manifest,
                sealedPackage.Envelope with
                {
                    PackageId = "different-package",
                }),
            CapturePackageFailureKind.EnvelopeMismatch);

        var artifactsByPath = artifacts.ToDictionary(
            static candidate => candidate.Path,
            StringComparer.Ordinal);
        await AssertCaptureFailureAsync(
            "response validation requires a bound inventory entry",
            () => CapturePackageContract.ValidateResponseShardsAsync(
                sealedDirectory.Path,
                fixture.Definition,
                artifactsByPath,
                CancellationToken.None,
                new Dictionary<string, TierZeroPackageFile>(
                    StringComparer.Ordinal)),
            CapturePackageFailureKind.TierZeroVerificationFailed);
    }

    [Fact]
    public async Task ReaderAndVerifierPropagateCancellationAndPathFailures()
    {
        using var missingDirectory =
            new PackageDirectory("capture-missing-reader-root");
        var missing = await Assert.ThrowsAsync<
            CapturePackageException>(
            () => new CapturePackageReader().LoadAsync(
                missingDirectory.Path));
        Assert.Equal(
            CapturePackageFailureKind.TierZeroVerificationFailed,
            missing.Kind);

        using var unsealedDirectory =
            new PackageDirectory("capture-unsealed-reader");
        var fixture = CreateFixture();
        _ = await CapturePackageWriter.CreateAsync(
            unsealedDirectory.Path,
            fixture.Envelope);
        var unsealed = await Assert.ThrowsAsync<
            CapturePackageException>(
            () => new CapturePackageReader().LoadAsync(
                unsealedDirectory.Path));
        Assert.Equal(
            CapturePackageFailureKind.TierZeroVerificationFailed,
            unsealed.Kind);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TierZeroPackageFileEnumerator.Enumerate(
                unsealedDirectory.Path,
                maximumEntries: 0));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            TierZeroPackageFileEnumerator.Enumerate(
                unsealedDirectory.Path,
                cancellationToken: cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => TierZeroPackageVerifier.VerifyAsync(
                unsealedDirectory.Path,
                cancellationToken: cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new CapturePackageReader().LoadAsync(
                unsealedDirectory.Path,
                cancellation.Token));

        var missingVerification =
            await TierZeroPackageVerifier.VerifyAsync(
                missingDirectory.Path);
        Assert.False(missingVerification.IsValid);
        var failure = Assert.Single(
            missingVerification.Failures);
        Assert.Equal(
            TierZeroVerificationFailureKind.PackageNotFound,
            failure.Kind);
        Assert.Null(failure.Path);

        var reservedDevice = Assert.Throws<
            TierZeroPackageException>(
            () => TierZeroPackagePath.Normalize("COM1"));
        Assert.Equal(
            TierZeroPackageError.InvalidPath,
            reservedDevice.Error);
        var namespaceCollision = Assert.Throws<
            TierZeroPackageException>(
            () => TierZeroPackagePath.ValidatePortableNamespace(
            [
                "capture/item.json",
                "capture",
            ]));
        Assert.Equal(
            TierZeroPackageError.DuplicateArtifactPath,
            namespaceCollision.Error);
        if (!OperatingSystem.IsWindows())
        {
            var physicalAlias = Assert.Throws<
                TierZeroPackageException>(
                () => TierZeroPackagePath
                    .NormalizePhysicalRelativePath(
                        "capture\\item.json"));
            Assert.Equal(
                TierZeroPackageError.InvalidPath,
                physicalAlias.Error);
        }
    }

    [Fact]
    public void ProductionScaleDescriptorsFitLimitsAndValidateBoundedly()
    {
        const int scopeCount = 8_500;
        const int pagesPerScope = 72;
        const int requestCount = scopeCount * pagesPerScope;
        const int responsesPerShard = 1_024;
        const int responseLength = 256;
        const long maximumValidationAllocation =
            1536L * 1024 * 1024;
        var definition = CreateScaleDefinition(
            scopeCount,
            pagesPerScope,
            responsesPerShard,
            responseLength);

        var allocatedBefore =
            GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var normalized =
            CapturePackageContract.ValidateDefinition(definition);
        stopwatch.Stop();
        var allocated =
            GC.GetAllocatedBytesForCurrentThread() -
            allocatedBefore;

        Assert.Equal(requestCount, normalized.Requests.Count);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(60),
            $"Production-scale validation took {stopwatch.Elapsed}.");
        Assert.True(
            allocated < maximumValidationAllocation,
            $"Production-scale validation allocated {allocated:N0} bytes.");

        var requestMeasurement =
            CapturePackageContract.MeasureRequestPlan(normalized);
        var scopeMeasurement =
            CapturePackageContract.MeasureScopeCompleteness(
                normalized);
        Assert.True(
            requestMeasurement.Bytes <
            CapturePackageFormat.MaximumDescriptorSetBytes);
        Assert.True(
            scopeMeasurement.Bytes <
            CapturePackageFormat.MaximumDescriptorSetBytes);

        var catalogBytes =
            CapturePackageContract.SerializeCatalog(
                normalized.Catalog);
        var catalogIdentity =
            CapturePackageContract.CreateCatalogIdentity(
                normalized.Catalog,
                catalogBytes);
        var envelope = CreateEnvelope(catalogBytes);
        var manifest = CapturePackageContract.CreateManifest(
            envelope,
            normalized,
            catalogIdentity,
            CapturePackageContract.CreateReference(
                CapturePackageFormat.RequestPlanPath,
                requestMeasurement),
            CapturePackageContract.CreateReference(
                CapturePackageFormat.ScopeCompletenessPath,
                scopeMeasurement));
        var manifestBytes =
            CapturePackageContract.SerializeManifest(manifest);
        var artifacts = new List<TierZeroArtifactDescriptor>
        {
            CapturePackageContract.ExpectedCatalogArtifact(
                catalogIdentity),
        };
        foreach (var layout in
                 CapturePackageContract.BuildResponseShardLayouts(
                     normalized.Requests))
        {
            artifacts.Add(new TierZeroArtifactDescriptor(
                CapturePackageFormat.ResponseShardOwner,
                layout.Path,
                CapturePackageFormat.JsonLinesMediaType,
                CapturePackageFormat.ResponseSchemaVersion,
                layout.RecordCount,
                [],
                layout.Bytes,
                layout.Bytes,
                Hash($"shard-{layout.Ordinal}")));
        }
        artifacts.Add(
            CapturePackageContract.ExpectedMetadataArtifact(
                CapturePackageFormat.RequestPlanOwner,
                CapturePackageFormat.RequestPlanPath,
                CapturePackageFormat.JsonLinesMediaType,
                requestMeasurement));
        artifacts.Add(
            CapturePackageContract.ExpectedMetadataArtifact(
                CapturePackageFormat.ScopeCompletenessOwner,
                CapturePackageFormat.ScopeCompletenessPath,
                CapturePackageFormat.JsonLinesMediaType,
                scopeMeasurement));
        artifacts.Add(
            CapturePackageContract.ExpectedMetadataArtifact(
                CapturePackageFormat.ManifestOwner,
                CapturePackageFormat.ManifestPath,
                CapturePackageFormat.JsonMediaType,
                1,
                manifestBytes));
        artifacts = artifacts
            .OrderBy(
                static artifact => artifact.Path,
                StringComparer.Ordinal)
            .ToList();
        CapturePackageContract.ValidateContentArtifactDescriptors(
            normalized,
            catalogIdentity,
            artifacts,
            requireMetadata: true);

        var state = TierZeroPackageModel.NormalizeState(
            new TierZeroPackageState(
                envelope,
                TierZeroPhasePlan.FromCurrentCatalog(),
                artifacts,
                null,
                TierZeroPackageStatus.Draft,
                null,
                null));
        var stateBytes = TierZeroCanonicalJson.Serialize(state);
        var checksumBytes =
            TierZeroPackageWriter.CreateChecksumManifest(
                artifacts);
        var checksum = new TierZeroChecksumManifest(
            TierZeroEvidenceFormat.ChecksumFileName,
            "sha256",
            artifacts.Count,
            Hash(checksumBytes));
        var envelopeManifest = TierZeroPackageModel.NormalizeManifest(
            new TierZeroEvidenceManifest(
                TierZeroEvidenceFormat.FormatId,
                TierZeroEvidenceFormat.ManifestVersion,
                envelope.PackageId,
                envelope.Source,
                envelope.Build,
                envelope.Database,
                envelope.Configuration,
                state.PhasePlan,
                envelope.SummaryReferences,
                artifacts,
                envelope.ParentRootHashes,
                envelope.Attempt,
                envelope.ProducerIdentity,
                envelope.CreatedAtUtc,
                CreatedAt.AddMinutes(7),
                TierZeroPackageStatus.Sealed,
                null,
                Hash(stateBytes),
                checksum,
                null));
        var envelopeBytes =
            TierZeroCanonicalJson.SerializeSealedManifest(
                envelopeManifest);

        Assert.True(stateBytes.LongLength < 4L * 1024 * 1024);
        Assert.True(envelopeBytes.LongLength < 4L * 1024 * 1024);
        Assert.True(checksumBytes.LongLength < 16L * 1024 * 1024);
        Assert.True(artifacts.Count < requestCount / 100);
    }

    private static CaptureFixture CreateFixture(
        bool reverseEnvelopeInputs = false)
    {
        var catalog = CreateCatalog(
            static (songId, kind, leaderboardType) =>
                songId == "song-b" &&
                kind == CaptureScopeKind.Band &&
                leaderboardType == "Band_Duets"
                    ? CaptureCatalogSupportStatus.Unsupported
                    : CaptureCatalogSupportStatus.Supported);
        var catalogBytes =
            CapturePackageContract.SerializeCatalog(catalog);
        var responses = new[]
        {
            CreateResponse(
                requestOrdinal: 0,
                scopeOrdinal: 0,
                CaptureScopeKind.Solo,
                "song-a",
                "Solo_Guitar",
                totalPages: 1,
                totalEntries: 2,
                [
                    Entry("""{"accountId":"a","rank":1}"""),
                    Entry("""{"accountId":"b","rank":2}"""),
                ]),
            CreateResponse(
                requestOrdinal: 1,
                scopeOrdinal: 1,
                CaptureScopeKind.Band,
                "song-a",
                "Band_Duets",
                totalPages: 1,
                totalEntries: 1,
                [
                    Entry("""{"rank":1,"teamKey":"a:b"}"""),
                ]),
            CreateResponse(
                requestOrdinal: 2,
                scopeOrdinal: 2,
                CaptureScopeKind.Solo,
                "song-b",
                "Solo_Guitar",
                totalPages: 0,
                totalEntries: 0,
                []),
        };
        var responseBytes = responses
            .Select(CapturePackageContract.SerializeResponse)
            .ToArray();
        var shardBytes = CombineJsonLines(responseBytes);
        var requests = CreateRequestDescriptors(
            responses,
            responseBytes);
        var scopes = new[]
        {
            ScopeFromRequests(0, requests, 0, 1),
            ScopeFromRequests(1, requests, 1, 1),
            ScopeFromRequests(2, requests, 2, 1),
            new CaptureScopeDescriptor(
                3,
                "song-b",
                CaptureScopeKind.Band,
                "Band_Duets",
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                CaptureScopeStatus.Unsupported,
                CapturePackageContract
                    .ComputeScopeContentSha256(
                        Array.Empty<
                            CaptureRequestDescriptor>())),
        };
        var definition = new CapturePackageDefinition(
            "capture-0001",
            catalog,
            ["Solo_Guitar"],
            ["Band_Duets"],
            CreatedAt.AddMinutes(1),
            CreatedAt.AddMinutes(6),
            scopes.Length,
            requests.Length,
            requests.Sum(static request =>
                request.CapturedEntryCount),
            requests.Sum(static request =>
                request.CapturedRequestCount),
            requests.Sum(static request =>
                request.CapturedResponseBytes),
            1,
            requests,
            scopes,
            CapturePackageStatus.Complete);
        return new CaptureFixture(
            CreateEnvelope(
                catalogBytes,
                reverseEnvelopeInputs),
            definition,
            catalogBytes,
            responses,
            shardBytes);
    }

    private static CaptureCatalogArtifact CreateCatalog(
        Func<string,
            CaptureScopeKind,
            string,
            CaptureCatalogSupportStatus> support)
    {
        var songs = new[] { "song-a", "song-b" }
            .Select(songId =>
                new CaptureCatalogSong(
                    songId,
                    CreateSupportRecords(
                        songId,
                        support)))
            .ToArray();
        return new CaptureCatalogArtifact(
            CapturePackageFormat.CatalogFormatId,
            CapturePackageFormat.CatalogSchemaVersion,
            42,
            songs.Length,
            songs);
    }

    private static IReadOnlyList<CaptureCatalogScopeSupport>
        CreateSupportRecords(
            string songId,
            Func<string,
                CaptureScopeKind,
                string,
                CaptureCatalogSupportStatus> support) =>
        CapturePackageFormat.SoloInstrumentOrder
            .Select(leaderboardType =>
                new CaptureCatalogScopeSupport(
                    CaptureScopeKind.Solo,
                    leaderboardType,
                    support(
                        songId,
                        CaptureScopeKind.Solo,
                        leaderboardType)))
            .Concat(
                CapturePackageFormat.BandTypeOrder.Select(
                    leaderboardType =>
                        new CaptureCatalogScopeSupport(
                            CaptureScopeKind.Band,
                            leaderboardType,
                            support(
                                songId,
                                CaptureScopeKind.Band,
                                leaderboardType))))
            .ToArray();

    private static CaptureResponseArtifact CreateResponse(
        int requestOrdinal,
        int scopeOrdinal,
        CaptureScopeKind scopeKind,
        string songId,
        string leaderboardType,
        int totalPages,
        long totalEntries,
        IReadOnlyList<JsonElement> entries) =>
        new(
            CapturePackageFormat.ResponseFormatId,
            CapturePackageFormat.ResponseSchemaVersion,
            requestOrdinal,
            scopeOrdinal,
            0,
            scopeKind == CaptureScopeKind.Solo
                ? CaptureResponseKind.SoloLeaderboardPage
                : CaptureResponseKind.BandLeaderboardPage,
            songId,
            scopeKind,
            leaderboardType,
            0,
            100,
            totalPages,
            totalEntries,
            entries.Count,
            entries);

    private static CaptureRequestDescriptor[]
        CreateRequestDescriptors(
            IReadOnlyList<CaptureResponseArtifact> responses,
            IReadOnlyList<byte[]> responseBytes)
    {
        var requests =
            new CaptureRequestDescriptor[responses.Count];
        long offset = 0;
        for (var index = 0;
             index < responses.Count;
             index++)
        {
            var response = responses[index];
            var bytes = responseBytes[index];
            requests[index] = new CaptureRequestDescriptor(
                index,
                response.ScopeOrdinal,
                response.ScopeRequestOrdinal,
                response.SongId,
                response.ScopeKind,
                response.LeaderboardType,
                response.PageIndex,
                response.PageSize,
                response.ProviderReportedTotalPages,
                response.ProviderReportedTotalEntries,
                1,
                response.EntryCount,
                index == 1 ? 2 : 1,
                bytes.LongLength,
                CaptureRequestStatus.Complete,
                CapturePackageFormat.ResponseShardPath(0),
                offset,
                bytes.Length,
                Hash(bytes));
            offset = checked(offset + bytes.LongLength + 1);
        }
        return requests;
    }

    private static CaptureScopeDescriptor ScopeFromRequests(
        int scopeOrdinal,
        IReadOnlyList<CaptureRequestDescriptor> requests,
        int start,
        int count)
    {
        var first = requests[start];
        return new CaptureScopeDescriptor(
            scopeOrdinal,
            first.SongId,
            first.ScopeKind,
            first.LeaderboardType,
            first.ProviderReportedTotalPages,
            first.ProviderReportedTotalEntries,
            first.ProviderReportedTotalPages,
            first.ProviderReportedTotalEntries,
            requests.Skip(start).Take(count).Sum(
                static request =>
                    request.CapturedPageCount),
            requests.Skip(start).Take(count).Sum(
                static request =>
                    request.CapturedEntryCount),
            requests.Skip(start).Take(count).Sum(
                static request =>
                    request.CapturedRequestCount),
            requests.Skip(start).Take(count).Sum(
                static request =>
                    request.CapturedResponseBytes),
            CaptureScopeStatus.Complete,
            CapturePackageContract.ComputeScopeContentSha256(
                requests,
                start,
                count));
    }

    private static CaptureFixture ReplaceResponses(
        CaptureFixture fixture,
        IReadOnlyList<CaptureResponseArtifact> responses)
    {
        var responseBytes = responses
            .Select(CapturePackageContract.SerializeResponse)
            .ToArray();
        var requests = fixture.Definition.Requests.ToArray();
        long offset = 0;
        for (var index = 0;
             index < requests.Length;
             index++)
        {
            requests[index] = requests[index] with
            {
                ResponseOffset = offset,
                ResponseLength = responseBytes[index].Length,
                CapturedResponseBytes =
                    responseBytes[index].LongLength,
                ContentSha256 = Hash(responseBytes[index]),
            };
            offset = checked(
                offset +
                responseBytes[index].LongLength +
                1);
        }
        var definition = RebuildDefinition(
            fixture,
            requests);
        return fixture with
        {
            Definition = definition,
            Responses = responses,
            ResponseShardBytes =
                CombineJsonLines(responseBytes),
        };
    }

    private static CapturePackageDefinition RebuildDefinition(
        CaptureFixture fixture,
        IReadOnlyList<CaptureRequestDescriptor> requests)
    {
        var scopes = fixture.Definition.Scopes
            .Select(scope =>
            {
                if (scope.Status ==
                    CaptureScopeStatus.Unsupported)
                {
                    return scope;
                }
                var start = requests
                    .TakeWhile(request =>
                        request.ScopeOrdinal <
                        scope.Ordinal)
                    .Count();
                var count = requests.Count(request =>
                    request.ScopeOrdinal ==
                    scope.Ordinal);
                return ScopeFromRequests(
                    scope.Ordinal,
                    requests,
                    start,
                    count);
            })
            .ToArray();
        return fixture.Definition with
        {
            TotalPageCount = requests.Count,
            TotalEntryCount = requests.Sum(static request =>
                request.CapturedEntryCount),
            TotalRequestCount = requests.Sum(static request =>
                request.CapturedRequestCount),
            TotalResponseBytes = requests.Sum(static request =>
                request.CapturedResponseBytes),
            Requests = requests,
            Scopes = scopes,
        };
    }

    private static CapturePackageDefinition CreateScaleDefinition(
        int scopeCount,
        int pagesPerScope,
        int responsesPerShard,
        int responseLength)
    {
        var sharedSupport = CreateSupportRecords(
            "scale",
            static (_, kind, leaderboardType) =>
                kind == CaptureScopeKind.Solo &&
                leaderboardType == "Solo_Guitar"
                    ? CaptureCatalogSupportStatus.Supported
                    : CaptureCatalogSupportStatus.Unsupported);
        var songs = Enumerable.Range(0, scopeCount)
            .Select(index =>
                new CaptureCatalogSong(
                    $"song-{index:D5}",
                    sharedSupport))
            .ToArray();
        var catalog = new CaptureCatalogArtifact(
            CapturePackageFormat.CatalogFormatId,
            CapturePackageFormat.CatalogSchemaVersion,
            20260912,
            songs.Length,
            songs);
        var requests = new List<CaptureRequestDescriptor>(
            scopeCount * pagesPerScope);
        var scopes = new List<CaptureScopeDescriptor>(
            scopeCount);
        var responseHash = Hash("scale-response-member");
        for (var scopeOrdinal = 0;
             scopeOrdinal < scopeCount;
             scopeOrdinal++)
        {
            var start = requests.Count;
            for (var page = 0;
                 page < pagesPerScope;
                 page++)
            {
                var ordinal = requests.Count;
                var shardOrdinal =
                    ordinal / responsesPerShard;
                var shardMember =
                    ordinal % responsesPerShard;
                requests.Add(new CaptureRequestDescriptor(
                    ordinal,
                    scopeOrdinal,
                    page,
                    songs[scopeOrdinal].SongId,
                    CaptureScopeKind.Solo,
                    "Solo_Guitar",
                    page,
                    100,
                    pagesPerScope,
                    pagesPerScope,
                    1,
                    1,
                    1,
                    responseLength,
                    CaptureRequestStatus.Complete,
                    CapturePackageFormat.ResponseShardPath(
                        shardOrdinal),
                    (long)shardMember *
                    (responseLength + 1L),
                    responseLength,
                    responseHash));
            }
            scopes.Add(new CaptureScopeDescriptor(
                scopeOrdinal,
                songs[scopeOrdinal].SongId,
                CaptureScopeKind.Solo,
                "Solo_Guitar",
                pagesPerScope,
                pagesPerScope,
                pagesPerScope,
                pagesPerScope,
                pagesPerScope,
                pagesPerScope,
                pagesPerScope,
                (long)pagesPerScope * responseLength,
                CaptureScopeStatus.Complete,
                CapturePackageContract
                    .ComputeScopeContentSha256(
                        requests,
                        start,
                        pagesPerScope)));
        }

        return new CapturePackageDefinition(
            "capture-production-scale",
            catalog,
            ["Solo_Guitar"],
            [],
            CreatedAt.AddMinutes(1),
            CreatedAt.AddMinutes(6),
            scopes.Count,
            requests.Count,
            requests.Count,
            requests.Count,
            (long)requests.Count * responseLength,
            (requests.Count + responsesPerShard - 1) /
                responsesPerShard,
            requests,
            scopes,
            CapturePackageStatus.Complete);
    }

    private static CapturePackageManifest CreateManifest(
        CaptureFixture fixture)
    {
        var normalized =
            CapturePackageContract.ValidateDefinition(
                fixture.Definition);
        var requestMeasurement =
            CapturePackageContract.MeasureRequestPlan(normalized);
        var scopeMeasurement =
            CapturePackageContract.MeasureScopeCompleteness(
                normalized);
        return CapturePackageContract.DeserializeManifest(
            CapturePackageContract.SerializeManifest(
                CapturePackageContract.CreateManifest(
                    fixture.Envelope,
                    normalized,
                    CapturePackageContract.CreateCatalogIdentity(
                        normalized.Catalog,
                        fixture.CatalogBytes),
                    CapturePackageContract.CreateReference(
                        CapturePackageFormat.RequestPlanPath,
                        requestMeasurement),
                    CapturePackageContract.CreateReference(
                        CapturePackageFormat.ScopeCompletenessPath,
                        scopeMeasurement))));
    }

    private static byte[] CreateManifestBytes(
        CaptureFixture fixture) =>
        CapturePackageContract.SerializeManifest(
            CreateManifest(fixture));

    private static async Task<byte[]> SerializeRequestPlanAsync(
        CapturePackageDefinition definition)
    {
        await using var stream =
            CapturePackageContract.OpenRequestPlanStream(
                definition.Requests);
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }

    private static async Task<byte[]>
        SerializeScopeCompletenessAsync(
            CapturePackageDefinition definition)
    {
        await using var stream =
            CapturePackageContract.OpenScopeCompletenessStream(
                definition.Scopes);
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }

    private static async Task AddContentArtifactsAsync(
        CapturePackageWriter writer,
        CaptureFixture fixture)
    {
        await writer.AddArtifactAsync(
            CatalogRegistration(fixture),
            fixture.CatalogBytes);
        await writer.AddArtifactAsync(
            ResponseRegistration(
                CapturePackageFormat.ResponseShardPath(0),
                fixture.Responses.Count,
                fixture.ResponseShardBytes.LongLength),
            fixture.ResponseShardBytes);
    }

    private static async Task AddContentArtifactsAsync(
        TierZeroPackageWriter writer,
        CaptureFixture fixture)
    {
        await writer.AddArtifactAsync(
            CatalogRegistration(fixture),
            fixture.CatalogBytes);
        await writer.AddArtifactAsync(
            ResponseRegistration(
                CapturePackageFormat.ResponseShardPath(0),
                fixture.Responses.Count,
                fixture.ResponseShardBytes.LongLength),
            fixture.ResponseShardBytes);
    }

    private static async Task SealUncheckedCaptureAsync(
        string rootPath,
        CaptureFixture fixture,
        string? requestPlanOwner = null)
    {
        var writer = await TierZeroPackageWriter.CreateAsync(
            rootPath,
            fixture.Envelope);
        await AddContentArtifactsAsync(writer, fixture);
        var normalized =
            CapturePackageContract.ValidateDefinition(
                fixture.Definition);
        var requestMeasurement =
            CapturePackageContract.MeasureRequestPlan(normalized);
        var scopeMeasurement =
            CapturePackageContract.MeasureScopeCompleteness(
                normalized);
        var requestBytes =
            await SerializeRequestPlanAsync(normalized);
        var scopeBytes =
            await SerializeScopeCompletenessAsync(normalized);
        await writer.AddArtifactAsync(
            new TierZeroArtifactRegistration(
                requestPlanOwner ??
                    CapturePackageFormat.RequestPlanOwner,
                CapturePackageFormat.RequestPlanPath,
                CapturePackageFormat.JsonLinesMediaType,
                CapturePackageFormat.DescriptorSchemaVersion,
                requestMeasurement.RecordCount,
                requestMeasurement.Bytes),
            requestBytes);
        await writer.AddArtifactAsync(
            new TierZeroArtifactRegistration(
                CapturePackageFormat.ScopeCompletenessOwner,
                CapturePackageFormat.ScopeCompletenessPath,
                CapturePackageFormat.JsonLinesMediaType,
                CapturePackageFormat.DescriptorSchemaVersion,
                scopeMeasurement.RecordCount,
                scopeMeasurement.Bytes),
            scopeBytes);
        var manifest = CapturePackageContract.CreateManifest(
            fixture.Envelope,
            normalized,
            CapturePackageContract.CreateCatalogIdentity(
                normalized.Catalog,
                fixture.CatalogBytes),
            CapturePackageContract.CreateReference(
                CapturePackageFormat.RequestPlanPath,
                requestMeasurement),
            CapturePackageContract.CreateReference(
                CapturePackageFormat.ScopeCompletenessPath,
                scopeMeasurement));
        var manifestBytes =
            CapturePackageContract.SerializeManifest(manifest);
        await writer.AddArtifactAsync(
            new TierZeroArtifactRegistration(
                CapturePackageFormat.ManifestOwner,
                CapturePackageFormat.ManifestPath,
                CapturePackageFormat.JsonMediaType,
                CapturePackageFormat.DescriptorSchemaVersion,
                1,
                manifestBytes.LongLength),
            manifestBytes);
        await writer.SealAsync(CreatedAt.AddMinutes(7));
    }

    private static TierZeroArtifactRegistration CatalogRegistration(
        CaptureFixture fixture) =>
        new(
            CapturePackageFormat.CatalogOwner,
            CapturePackageFormat.CatalogPath,
            CapturePackageFormat.JsonMediaType,
            CapturePackageFormat.CatalogSchemaVersion,
            fixture.Definition.Catalog.SongCount,
            fixture.CatalogBytes.LongLength);

    private static TierZeroArtifactRegistration ResponseRegistration(
        string path,
        long rowCount,
        long bytes) =>
        new(
            CapturePackageFormat.ResponseShardOwner,
            path,
            CapturePackageFormat.JsonLinesMediaType,
            CapturePackageFormat.ResponseSchemaVersion,
            rowCount,
            bytes);

    private static TierZeroPackageDraft CreateEnvelope(
        byte[] catalogBytes,
        bool reverseInputs = false)
    {
        var configurationValues = reverseInputs
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
        var parents = new[]
        {
            new TierZeroParentRootHash(
                "catalog-source",
                Hash("catalog-source")),
            new TierZeroParentRootHash(
                "capture-plan",
                Hash("capture-plan")),
        };
        return new TierZeroPackageDraft(
            "capture-package-0001",
            new TierZeroSourceIdentity(
                null,
                null,
                CreatedAt,
                new TierZeroCatalogIdentity(
                    "festival-catalog-42",
                    Hash(catalogBytes))),
            new TierZeroBuildIdentity(
                new string('a', 40),
                $"sha256:{Hash("image")}",
                new string('b', 40),
                "1.0.202"),
            new TierZeroDatabaseIdentity(
                17,
                reverseInputs
                    ? ["pg_trgm@1.6", "btree_gin@1.3"]
                    : ["btree_gin@1.3", "pg_trgm@1.6"],
                Hash("schema")),
            TierZeroConfigurationFingerprinter.Create(
                configurationValues,
                reverseInputs
                    ? [
                        "Scraper:SequentialScrape",
                        "Scraper:PageConcurrency",
                    ]
                    : [
                        "Scraper:PageConcurrency",
                        "Scraper:SequentialScrape",
                    ]),
            TierZeroSummaryReferences.Empty,
            reverseInputs
                ? parents.Reverse().ToArray()
                : parents,
            1,
            "capture-package-contract-tests",
            CreatedAt);
    }

    private static TierZeroResumeExpectations ResumeExpectations(
        TierZeroPackageDraft envelope) =>
        new(
            envelope.PackageId,
            envelope.Attempt,
            envelope.ProducerIdentity,
            envelope.ParentRootHashes,
            envelope.Configuration.ValuesSha256,
            envelope.Database.SchemaFingerprint,
            PhaseProgressCatalog.OperationId,
            PhaseProgressCatalog.PlanVersion);

    private static byte[] CombineJsonLines(
        IReadOnlyList<byte[]> rows)
    {
        using var stream = new MemoryStream();
        foreach (var row in rows)
        {
            stream.Write(row);
            stream.WriteByte((byte)'\n');
        }
        return stream.ToArray();
    }

    private static JsonElement Entry(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void AssertFailure(
        CapturePackageDefinition definition,
        CapturePackageFailureKind expected)
    {
        var exception = Assert.Throws<CapturePackageException>(
            () => CapturePackageContract.ValidateDefinition(
                definition));
        Assert.Equal(expected, exception.Kind);
    }

    private static void AssertCaptureFailure(
        string name,
        Action action,
        CapturePackageFailureKind expected)
    {
        var exception = Record.Exception(action);
        var captureException =
            Assert.IsType<CapturePackageException>(exception);
        Assert.True(
            captureException.Kind == expected,
            $"{name}: expected {expected}, got " +
            $"{captureException.Kind}: {captureException.Message}");
    }

    private static async Task AssertCaptureFailureAsync(
        string name,
        Func<Task> action,
        CapturePackageFailureKind expected)
    {
        var exception = await Record.ExceptionAsync(action);
        var captureException =
            Assert.IsType<CapturePackageException>(exception);
        Assert.True(
            captureException.Kind == expected,
            $"{name}: expected {expected}, got " +
            $"{captureException.Kind}: {captureException.Message}");
    }

    private static CapturePackageDefinition WithScope(
        CapturePackageDefinition definition,
        int index,
        Func<CaptureScopeDescriptor, CaptureScopeDescriptor>
            mutate)
    {
        var scopes = definition.Scopes.ToArray();
        scopes[index] = mutate(scopes[index]);
        return definition with { Scopes = scopes };
    }

    private static CapturePackageDefinition WithRequest(
        CapturePackageDefinition definition,
        int index,
        Func<CaptureRequestDescriptor, CaptureRequestDescriptor>
            mutate)
    {
        var requests = definition.Requests.ToArray();
        requests[index] = mutate(requests[index]);
        return definition with { Requests = requests };
    }

    private static string Hash(string value) =>
        Hash(Encoding.UTF8.GetBytes(value));

    private static string Hash(byte[] value) =>
        TierZeroCanonicalJson.Sha256Hex(value);

    private sealed record JsonLinesFailureCase(
        string Name,
        byte[] Bytes,
        long ExpectedBytes,
        int ExpectedCount,
        int MaximumRecords,
        long MaximumBytes,
        int MaximumRecordBytes,
        CapturePackageFailureKind ExpectedFailure);

    private sealed record CaptureFixture(
        TierZeroPackageDraft Envelope,
        CapturePackageDefinition Definition,
        byte[] CatalogBytes,
        IReadOnlyList<CaptureResponseArtifact> Responses,
        byte[] ResponseShardBytes);

    private sealed class PackageDirectory : IDisposable
    {
        public PackageDirectory(string name)
        {
            Path = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "capture-package-tests",
                $"{name}-{Guid.NewGuid():N}");
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
