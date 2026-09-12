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

    private static string Hash(string value) =>
        Hash(Encoding.UTF8.GetBytes(value));

    private static string Hash(byte[] value) =>
        TierZeroCanonicalJson.Sha256Hex(value);

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
