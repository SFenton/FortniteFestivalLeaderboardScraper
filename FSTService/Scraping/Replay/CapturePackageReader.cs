namespace FSTService.Scraping.Replay;

public sealed class CapturePackageReader
{
    public async Task<CapturePackage> LoadAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(rootPath);
        TierZeroPackageInventory initialInventory;
        try
        {
            TierZeroPackagePath.EnsureNoSymbolicLinks(
                root,
                root,
                includeCandidate: true);
            initialInventory =
                TierZeroPackageFileEnumerator.Enumerate(
                    root,
                    CapturePackageFormat
                        .MaximumPackageFileSystemEntries,
                    cancellationToken);
        }
        catch (TierZeroPackageException exception)
        {
            throw new CapturePackageException(
                CapturePackageFailureKind
                    .TierZeroVerificationFailed,
                "Capture package filesystem preflight failed.",
                exception);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException)
        {
            throw new CapturePackageException(
                CapturePackageFailureKind
                    .TierZeroVerificationFailed,
                "Capture package filesystem preflight failed.",
                exception);
        }

        var firstVerification =
            await TierZeroPackageVerifier.VerifyStructureAsync(
                root,
                initialInventory,
                maximumFileSystemEntries:
                    CapturePackageFormat
                        .MaximumPackageFileSystemEntries,
                cancellationToken: cancellationToken);
        var envelope = RequireValidEnvelope(firstVerification);
        var initialFiles = initialInventory.Files.ToDictionary(
            static file => file.RelativePath,
            StringComparer.Ordinal);
        var byPath = envelope.Artifacts.ToDictionary(
            static artifact => artifact.Path,
            StringComparer.Ordinal);

        var manifestArtifact =
            CapturePackageContract.RequireArtifact(
                byPath,
                CapturePackageFormat.ManifestPath);
        var manifestBytes =
            await CapturePackageContract.ReadArtifactBytesAsync(
                root,
                manifestArtifact,
                CapturePackageFormat.MaximumManifestBytes,
                cancellationToken,
                ExpectedSnapshot(
                    initialFiles,
                    manifestArtifact.Path));
        var manifest =
            CapturePackageContract.DeserializeManifest(
                manifestBytes);
        CapturePackageContract.ValidateEnvelope(manifest, envelope);
        CapturePackageContract.PreflightEnvelopeArtifacts(
            manifest,
            envelope.Artifacts);
        CapturePackageContract.RequireArtifact(
            byPath,
            CapturePackageContract.ExpectedMetadataArtifact(
                CapturePackageFormat.ManifestOwner,
                CapturePackageFormat.ManifestPath,
                CapturePackageFormat.JsonMediaType,
                1,
                manifestBytes));

        var catalogArtifact =
            CapturePackageContract.RequireArtifact(
                byPath,
                CapturePackageFormat.CatalogPath);
        var catalogBytes =
            await CapturePackageContract.ReadArtifactBytesAsync(
                root,
                catalogArtifact,
                CapturePackageFormat.MaximumCatalogBytes,
                cancellationToken,
                ExpectedSnapshot(
                    initialFiles,
                    catalogArtifact.Path));
        var catalog =
            CapturePackageContract.DeserializeCatalog(
                catalogBytes);
        var catalogIdentity =
            CapturePackageContract.CreateCatalogIdentity(
                catalog,
                catalogBytes);
        if (!CapturePackageContract.CanonicalEquals(
                catalogIdentity,
                manifest.Catalog))
        {
            throw new CapturePackageException(
                CapturePackageFailureKind.ArtifactMismatch,
                "Capture catalog bytes do not match the manifest identity.");
        }

        var requestArtifact =
            CapturePackageContract.RequireArtifact(
                byPath,
                CapturePackageFormat.RequestPlanPath);
        CapturePackageContract.ValidateReference(
            manifest.RequestPlan,
            requestArtifact);
        var requests = new List<CaptureRequestDescriptor>(
            Math.Min(
                manifest.RequestPlan.RecordCount,
                4 * 1024));
        var requestMeasurement =
            await CapturePackageContract
                .ReadJsonLinesArtifactAsync<
                    CaptureRequestDescriptor>(
                    root,
                    requestArtifact,
                    manifest.RequestPlan.RecordCount,
                    manifest.RequestPlan.Bytes,
                    CapturePackageFormat.MaximumRequestRecords,
                    CapturePackageFormat.MaximumDescriptorSetBytes,
                    CapturePackageFormat.MaximumRequestDescriptorBytes,
                    "Capture request plan",
                    preDeserialize: null,
                    (request, _, _, _, _) =>
                        requests.Add(request),
                    cancellationToken,
                    ExpectedSnapshot(
                        initialFiles,
                        requestArtifact.Path));
        if (!string.Equals(
                requestMeasurement.Sha256,
                manifest.RequestPlan.Sha256,
                StringComparison.Ordinal))
        {
            throw new CapturePackageException(
                CapturePackageFailureKind.InvalidHash,
                "Capture request plan hash does not match its manifest reference.");
        }
        CapturePackageContract.RequireArtifact(
            byPath,
            CapturePackageContract.ExpectedMetadataArtifact(
                CapturePackageFormat.RequestPlanOwner,
                CapturePackageFormat.RequestPlanPath,
                CapturePackageFormat.JsonLinesMediaType,
                requestMeasurement));

        var scopeArtifact =
            CapturePackageContract.RequireArtifact(
                byPath,
                CapturePackageFormat.ScopeCompletenessPath);
        CapturePackageContract.ValidateReference(
            manifest.ScopeCompleteness,
            scopeArtifact);
        var scopes = new List<CaptureScopeDescriptor>(
            Math.Min(
                manifest.ScopeCompleteness.RecordCount,
                4 * 1024));
        var scopeMeasurement =
            await CapturePackageContract
                .ReadJsonLinesArtifactAsync<
                    CaptureScopeDescriptor>(
                    root,
                    scopeArtifact,
                    manifest.ScopeCompleteness.RecordCount,
                    manifest.ScopeCompleteness.Bytes,
                    CapturePackageFormat.MaximumScopeRecords,
                    CapturePackageFormat.MaximumDescriptorSetBytes,
                    CapturePackageFormat.MaximumScopeDescriptorBytes,
                    "Capture scope completeness",
                    preDeserialize: null,
                    (scope, _, _, _, _) =>
                        scopes.Add(scope),
                    cancellationToken,
                    ExpectedSnapshot(
                        initialFiles,
                        scopeArtifact.Path));
        if (!string.Equals(
                scopeMeasurement.Sha256,
                manifest.ScopeCompleteness.Sha256,
                StringComparison.Ordinal))
        {
            throw new CapturePackageException(
                CapturePackageFailureKind.InvalidHash,
                "Capture scope completeness hash does not match its manifest reference.");
        }
        CapturePackageContract.RequireArtifact(
            byPath,
            CapturePackageContract.ExpectedMetadataArtifact(
                CapturePackageFormat.ScopeCompletenessOwner,
                CapturePackageFormat.ScopeCompletenessPath,
                CapturePackageFormat.JsonLinesMediaType,
                scopeMeasurement));

        var definition = CapturePackageContract.ValidateDefinition(
            CapturePackageContract.DefinitionFrom(
                manifest,
                catalog,
                requests,
                scopes));
        CapturePackageContract.ValidateContentArtifactDescriptors(
            definition,
            catalogIdentity,
            envelope.Artifacts,
            requireMetadata: true);
        await CapturePackageContract.ValidateResponseShardsAsync(
            root,
            definition,
            byPath,
            cancellationToken,
            initialFiles);

        var stabilityFailures =
            new List<TierZeroVerificationFailure>();
        TierZeroPackageVerifier.VerifyStableFinalInventory(
            root,
            initialInventory,
            stabilityFailures,
            CapturePackageFormat
                .MaximumPackageFileSystemEntries,
            cancellationToken);
        if (stabilityFailures.Count != 0)
        {
            throw new CapturePackageException(
                CapturePackageFailureKind.TierZeroVerificationFailed,
                "Capture package changed while it was being loaded: " +
                string.Join(
                    ",",
                    stabilityFailures.Select(static failure =>
                        failure.Kind)));
        }

        return new CapturePackage(
            envelope,
            manifest,
            catalog,
            definition.Requests,
            definition.Scopes);
    }

    private static TierZeroEvidenceManifest RequireValidEnvelope(
        TierZeroVerificationResult result)
    {
        if (!result.IsValid ||
            result.Manifest is null ||
            result.Manifest.Status != TierZeroPackageStatus.Sealed)
        {
            throw new CapturePackageException(
                CapturePackageFailureKind.TierZeroVerificationFailed,
                "Capture package Tier-0 verification failed: " +
                string.Join(
                    ",",
                    result.Failures.Select(static failure =>
                        failure.Kind)));
        }
        return result.Manifest;
    }

    private static TierZeroFileSnapshot ExpectedSnapshot(
        IReadOnlyDictionary<string, TierZeroPackageFile> files,
        string path) =>
        files.TryGetValue(path, out var file)
            ? file.Snapshot
            : throw new CapturePackageException(
                CapturePackageFailureKind
                    .TierZeroVerificationFailed,
                $"Capture package inventory is missing '{path}'.");
}
