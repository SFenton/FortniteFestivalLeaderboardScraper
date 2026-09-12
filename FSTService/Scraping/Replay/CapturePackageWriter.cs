namespace FSTService.Scraping.Replay;

public sealed class CapturePackageWriter
{
    private readonly TierZeroPackageWriter _writer;

    private CapturePackageWriter(TierZeroPackageWriter writer)
    {
        _writer = writer;
    }

    public string RootPath => _writer.RootPath;
    public bool IsSealed => _writer.IsSealed;
    public IReadOnlyList<TierZeroArtifactDescriptor> Artifacts =>
        _writer.Artifacts;

    public static async Task<CapturePackageWriter> CreateAsync(
        string rootPath,
        TierZeroPackageDraft envelope,
        CancellationToken cancellationToken = default) =>
        new(await TierZeroPackageWriter.CreateAsync(
            rootPath,
            envelope,
            cancellationToken));

    public static async Task<CapturePackageWriter> ResumeAsync(
        string rootPath,
        TierZeroResumeExpectations expectations,
        CancellationToken cancellationToken = default) =>
        new(await TierZeroPackageWriter.ResumeAsync(
            rootPath,
            expectations,
            cancellationToken));

    public async Task<TierZeroArtifactDescriptor> AddArtifactAsync(
        TierZeroArtifactRegistration registration,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var normalized =
            TierZeroPackageModel.NormalizeRegistration(registration);
        if (CapturePackageContract.IsCaptureMetadataPath(
                normalized.Path))
        {
            throw new CapturePackageException(
                CapturePackageFailureKind.ArtifactMismatch,
                $"Capture metadata artifact '{normalized.Path}' is owned by the capture package writer.");
        }
        var maximumBytes = string.Equals(
                normalized.Path,
                CapturePackageFormat.CatalogPath,
                StringComparison.Ordinal)
            ? CapturePackageFormat.MaximumCatalogBytes
            : CapturePackageFormat.TryParseResponseShardPath(
                normalized.Path,
                out _)
                ? CapturePackageFormat.MaximumResponseShardBytes
                : 0;
        if (maximumBytes == 0)
        {
            throw new CapturePackageException(
                CapturePackageFailureKind.ArtifactMismatch,
                $"Capture content artifact path '{normalized.Path}' is not part of the v1 closed set.");
        }
        if (content.Length <= 0)
        {
            throw new CapturePackageException(
                CapturePackageFailureKind.ArtifactMismatch,
                $"Capture content artifact '{normalized.Path}' cannot be empty.");
        }
        if (content.Length > maximumBytes)
        {
            throw new CapturePackageException(
                CapturePackageFailureKind.RecordLimitExceeded,
                $"Capture content artifact '{normalized.Path}' is outside its byte limit.");
        }

        var bytes = content.ToArray();
        await CapturePackageContract
            .ValidateContentArtifactForWriteAsync(
                registration,
                bytes,
                _writer.Draft,
                cancellationToken);
        await using var stream = new MemoryStream(
            bytes,
            writable: false);
        return await _writer.AddArtifactAsync(
            normalized,
            stream,
            static (artifacts, _) =>
            {
                CapturePackageContract
                    .EnsureContentAdditionAllowed(artifacts);
                return Task.CompletedTask;
            },
            cancellationToken);
    }

    public Task MarkInterruptedAsync(
        string error,
        DateTimeOffset? interruptedAtUtc = null,
        CancellationToken cancellationToken = default) =>
        _writer.MarkInterruptedAsync(
            error,
            interruptedAtUtc,
            cancellationToken);

    public Task<CapturePackageSealResult> SealAsync(
        CapturePackageDefinition definition,
        DateTimeOffset sealedAtUtc,
        CancellationToken cancellationToken = default) =>
        SealAsync(
            definition,
            sealedAtUtc,
            insidePackageLock: null,
            cancellationToken);

    internal async Task<CapturePackageSealResult> SealAsync(
        CapturePackageDefinition definition,
        DateTimeOffset sealedAtUtc,
        Func<CancellationToken, Task>? insidePackageLock,
        CancellationToken cancellationToken = default)
    {
        var normalized =
            CapturePackageContract.ValidateDefinition(definition);
        var normalizedSealedAtUtc = sealedAtUtc.ToUniversalTime();
        if (sealedAtUtc == default ||
            normalizedSealedAtUtc <
            normalized.CaptureCompletedAtUtc)
        {
            throw new CapturePackageException(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture package seal timestamp cannot precede capture completion.");
        }

        var catalogBytes =
            CapturePackageContract.SerializeCatalog(
                normalized.Catalog);
        var catalogIdentity =
            CapturePackageContract.CreateCatalogIdentity(
                normalized.Catalog,
                catalogBytes);
        if (!string.Equals(
                catalogIdentity.ContentSha256,
                _writer.Draft.Source.Catalog.ContentSha256,
                StringComparison.Ordinal))
        {
            throw new CapturePackageException(
                CapturePackageFailureKind.EnvelopeMismatch,
                "Capture catalog hash does not match the Tier-0 source catalog hash.");
        }

        var requestMeasurement =
            CapturePackageContract.MeasureRequestPlan(normalized);
        var scopeMeasurement =
            CapturePackageContract.MeasureScopeCompleteness(
                normalized);
        var requestReference =
            CapturePackageContract.CreateReference(
                CapturePackageFormat.RequestPlanPath,
                requestMeasurement);
        var scopeReference =
            CapturePackageContract.CreateReference(
                CapturePackageFormat.ScopeCompletenessPath,
                scopeMeasurement);

        await AddOrValidateMetadataAsync(
            CapturePackageFormat.RequestPlanOwner,
            CapturePackageFormat.RequestPlanPath,
            CapturePackageFormat.JsonLinesMediaType,
            requestMeasurement,
            () => CapturePackageContract.OpenRequestPlanStream(
                normalized.Requests),
            cancellationToken);
        await AddOrValidateMetadataAsync(
            CapturePackageFormat.ScopeCompletenessOwner,
            CapturePackageFormat.ScopeCompletenessPath,
            CapturePackageFormat.JsonLinesMediaType,
            scopeMeasurement,
            () => CapturePackageContract.OpenScopeCompletenessStream(
                normalized.Scopes),
            cancellationToken);

        var captureManifest =
            CapturePackageContract.CreateManifest(
                _writer.Draft,
                normalized,
                catalogIdentity,
                requestReference,
                scopeReference);
        var manifestBytes =
            CapturePackageContract.SerializeManifest(captureManifest);
        EnsureMaximumSize(
            manifestBytes,
            CapturePackageFormat.MaximumManifestBytes,
            "capture manifest");
        captureManifest =
            CapturePackageContract.DeserializeManifest(manifestBytes);
        await AddOrValidateMetadataAsync(
            CapturePackageFormat.ManifestOwner,
            CapturePackageFormat.ManifestPath,
            CapturePackageFormat.JsonMediaType,
            new CaptureJsonLinesMeasurement(
                1,
                manifestBytes.LongLength,
                TierZeroCanonicalJson.Sha256Hex(manifestBytes)),
            () => new MemoryStream(
                manifestBytes,
                writable: false),
            cancellationToken);

        var envelope = await _writer.SealAsync(
            normalizedSealedAtUtc,
            TierZeroPackageStatus.Sealed,
            error: null,
            async (artifacts, innerCancellationToken) =>
            {
                await CapturePackageContract
                    .ValidatePackageArtifactsAsync(
                        _writer.RootPath,
                        _writer.Draft,
                        normalized,
                        catalogIdentity,
                        captureManifest,
                        requestMeasurement,
                        scopeMeasurement,
                        artifacts,
                        innerCancellationToken);
                if (insidePackageLock is not null)
                {
                    await insidePackageLock(
                        innerCancellationToken);
                }
            },
            cancellationToken);
        CapturePackageContract.ValidateEnvelope(
            captureManifest,
            envelope);
        return new CapturePackageSealResult(
            envelope,
            captureManifest);
    }

    private async Task<TierZeroArtifactDescriptor>
        AddOrValidateMetadataAsync(
            string owner,
            string path,
            string mediaType,
            CaptureJsonLinesMeasurement measurement,
            Func<Stream> openContent,
            CancellationToken cancellationToken)
    {
        var expected =
            CapturePackageContract.ExpectedMetadataArtifact(
                owner,
                path,
                mediaType,
                measurement);
        var existing = FindArtifact(path);
        if (existing is not null)
        {
            CapturePackageContract.RequireArtifact(
                new Dictionary<string, TierZeroArtifactDescriptor>(
                    StringComparer.Ordinal)
                {
                    [path] = existing,
                },
                expected);
            return existing;
        }

        try
        {
            await using var content = openContent();
            var added = await _writer.AddArtifactAsync(
                new TierZeroArtifactRegistration(
                    owner,
                    path,
                    mediaType,
                    CapturePackageFormat.DescriptorSchemaVersion,
                    measurement.RecordCount,
                    measurement.Bytes),
                content,
                cancellationToken);
            CapturePackageContract.RequireArtifact(
                new Dictionary<string, TierZeroArtifactDescriptor>(
                    StringComparer.Ordinal)
                {
                    [path] = added,
                },
                expected);
            return added;
        }
        catch (TierZeroPackageException exception) when (
            exception.Error ==
            TierZeroPackageError.DuplicateArtifactPath)
        {
            existing = FindArtifact(path);
            if (existing is null)
                throw;
            CapturePackageContract.RequireArtifact(
                new Dictionary<string, TierZeroArtifactDescriptor>(
                    StringComparer.Ordinal)
                {
                    [path] = existing,
                },
                expected);
            return existing;
        }
    }

    private TierZeroArtifactDescriptor? FindArtifact(string path) =>
        _writer.Artifacts.SingleOrDefault(
            artifact => string.Equals(
                artifact.Path,
                path,
                StringComparison.Ordinal));

    private static void EnsureMaximumSize(
        ReadOnlyMemory<byte> bytes,
        long maximumBytes,
        string description)
    {
        if (bytes.Length <= 0 ||
            bytes.Length > maximumBytes)
        {
            throw new CapturePackageException(
                CapturePackageFailureKind.RecordLimitExceeded,
                $"{description} size is outside the supported contract bounds.");
        }
    }
}
