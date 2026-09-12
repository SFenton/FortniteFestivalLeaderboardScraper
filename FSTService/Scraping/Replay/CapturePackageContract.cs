using System.Diagnostics.CodeAnalysis;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace FSTService.Scraping.Replay;

public static class CapturePackageContract
{
    private static readonly IReadOnlyList<CaptureScopeKey>
        CanonicalScopeTypes =
        CapturePackageFormat.SoloInstrumentOrder
            .Select(static leaderboardType =>
                new CaptureScopeKey(
                    CaptureScopeKind.Solo,
                    leaderboardType))
            .Concat(
                CapturePackageFormat.BandTypeOrder.Select(
                    static leaderboardType =>
                        new CaptureScopeKey(
                            CaptureScopeKind.Band,
                            leaderboardType)))
            .ToArray();

    public static CaptureCatalogArtifact ValidateCatalog(
        CaptureCatalogArtifact catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.Songs is null)
        {
            Invalid(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture catalog is missing its song collection.");
        }
        if (!string.Equals(
                catalog.FormatId,
                CapturePackageFormat.CatalogFormatId,
                StringComparison.Ordinal) ||
            catalog.SchemaVersion !=
                CapturePackageFormat.CatalogSchemaVersion)
        {
            Invalid(
                CapturePackageFailureKind.UnsupportedFormat,
                "Capture catalog format identity is unsupported.");
        }
        if (catalog.CatalogVersion <= 0)
        {
            Invalid(
                CapturePackageFailureKind.NegativeCount,
                "Capture catalog version must be positive.");
        }
        ValidateRecordCount(
            catalog.SongCount,
            CapturePackageFormat.MaximumCatalogSongs,
            "catalog song");
        if (catalog.Songs.Count != catalog.SongCount)
        {
            Invalid(
                CapturePackageFailureKind.AggregateMismatch,
                "Capture catalog song count does not match its songs.");
        }

        var songs = new CaptureCatalogSong[catalog.SongCount];
        string? previousSongId = null;
        for (var songIndex = 0;
             songIndex < catalog.Songs.Count;
             songIndex++)
        {
            var song = catalog.Songs[songIndex];
            if (song is null ||
                song.ScopeSupport is null)
            {
                Invalid(
                    CapturePackageFailureKind.InvalidMetadata,
                    "Capture catalog songs and support records cannot be null.");
            }

            RequireSafeText(song.SongId, "catalog song ID");
            if (previousSongId is not null)
            {
                var comparison = string.Compare(
                    previousSongId,
                    song.SongId,
                    StringComparison.Ordinal);
                if (comparison == 0)
                {
                    Invalid(
                        CapturePackageFailureKind.DuplicateScope,
                        $"Capture catalog song ID '{song.SongId}' is duplicated.");
                }
                if (comparison > 0)
                {
                    Invalid(
                        CapturePackageFailureKind.NonCanonicalOrder,
                        "Capture catalog songs are not in ordinal song-ID order.");
                }
            }

            if (song.ScopeSupport.Count != CanonicalScopeTypes.Count)
            {
                Invalid(
                    CapturePackageFailureKind.MissingScope,
                    $"Capture catalog song '{song.SongId}' does not explicitly classify every v1 scope type.");
            }

            var support = new CaptureCatalogScopeSupport[
                CanonicalScopeTypes.Count];
            for (var supportIndex = 0;
                 supportIndex < CanonicalScopeTypes.Count;
                 supportIndex++)
            {
                var actual = song.ScopeSupport[supportIndex];
                var expected = CanonicalScopeTypes[supportIndex];
                if (actual is null)
                {
                    Invalid(
                        CapturePackageFailureKind.InvalidMetadata,
                        "Capture catalog support records cannot be null.");
                }
                if (!Enum.IsDefined(actual.Status))
                {
                    Invalid(
                        CapturePackageFailureKind.InvalidMetadata,
                        "Capture catalog support status is unsupported.");
                }
                RequireSafeText(
                    actual.LeaderboardType,
                    "catalog leaderboard type");
                if (actual.ScopeKind != expected.ScopeKind ||
                    !string.Equals(
                        actual.LeaderboardType,
                        expected.LeaderboardType,
                        StringComparison.Ordinal))
                {
                    Invalid(
                        CapturePackageFailureKind.NonCanonicalOrder,
                        $"Capture catalog support for song '{song.SongId}' is not the complete canonical v1 scope sequence.");
                }
                support[supportIndex] = actual;
            }

            songs[songIndex] = song with
            {
                ScopeSupport = support,
            };
            previousSongId = song.SongId;
        }

        return catalog with { Songs = songs };
    }

    public static byte[] SerializeCatalog(
        CaptureCatalogArtifact catalog) =>
        TierZeroCanonicalJson.Serialize(ValidateCatalog(catalog));

    public static CaptureCatalogArtifact DeserializeCatalog(
        ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length <= 0 ||
            bytes.Length > CapturePackageFormat.MaximumCatalogBytes)
        {
            Invalid(
                CapturePackageFailureKind.RecordLimitExceeded,
                "Capture catalog bytes are outside the supported bounds.");
        }
        PreflightJsonStructure(
            bytes,
            "songs",
            CapturePackageFormat.MaximumCatalogSongs,
            "capture catalog songs",
            new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["scopeSupport"] =
                    CanonicalScopeTypes.Count,
            });

        CaptureCatalogArtifact parsed;
        try
        {
            parsed =
                TierZeroCanonicalJson.Deserialize<CaptureCatalogArtifact>(
                    bytes);
        }
        catch (JsonException exception)
        {
            throw new CapturePackageException(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture catalog is invalid JSON.",
                exception);
        }

        var validated = ValidateCatalog(parsed);
        if (!bytes.SequenceEqual(
                TierZeroCanonicalJson.Serialize(validated)))
        {
            Invalid(
                CapturePackageFailureKind.NonCanonicalJson,
                "Capture catalog bytes are not canonical.");
        }
        return validated;
    }

    public static CaptureCatalogIdentity CreateCatalogIdentity(
        CaptureCatalogArtifact catalog)
    {
        var bytes = SerializeCatalog(catalog);
        return CreateCatalogIdentity(catalog, bytes);
    }

    public static CaptureResponseArtifact ValidateResponse(
        CaptureResponseArtifact response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Entries is null)
        {
            Invalid(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture response is missing its entries.");
        }
        if (!string.Equals(
                response.FormatId,
                CapturePackageFormat.ResponseFormatId,
                StringComparison.Ordinal) ||
            response.SchemaVersion !=
                CapturePackageFormat.ResponseSchemaVersion)
        {
            Invalid(
                CapturePackageFailureKind.UnsupportedFormat,
                "Capture response format identity is unsupported.");
        }
        RequireNonNegative(
            response.RequestOrdinal,
            "response request ordinal");
        RequireNonNegative(
            response.ScopeOrdinal,
            "response scope ordinal");
        RequireNonNegative(
            response.ScopeRequestOrdinal,
            "response scope request ordinal");
        RequireSafeText(response.SongId, "response song ID");
        ValidateKnownScopeKind(response.ScopeKind);
        ValidateKnownResponseKind(response.ResponseKind);
        if (response.ResponseKind !=
            ExpectedResponseKind(response.ScopeKind))
        {
            Invalid(
                CapturePackageFailureKind.AggregateMismatch,
                "Capture response kind does not match its scope kind.");
        }
        ValidateKnownLeaderboardType(
            response.ScopeKind,
            response.LeaderboardType);
        RequireNonNegative(response.PageIndex, "response page index");
        if (response.PageSize <= 0 ||
            response.PageSize > CapturePackageFormat.MaximumPageSize)
        {
            Invalid(
                CapturePackageFailureKind.RecordLimitExceeded,
                "Capture response page size is outside the supported bounds.");
        }
        RequireNonNegative(
            response.ProviderReportedTotalPages,
            "provider-reported response page count");
        RequireNonNegative(
            response.ProviderReportedTotalEntries,
            "provider-reported response entry count");
        if (response.ProviderReportedTotalPages >
            CapturePackageFormat.MaximumRequestRecords)
        {
            Invalid(
                CapturePackageFailureKind.RecordLimitExceeded,
                "Provider-reported response page count exceeds the capture limit.");
        }
        if (response.EntryCount < 0 ||
            response.EntryCount >
            CapturePackageFormat.MaximumResponseEntries ||
            response.EntryCount > response.PageSize ||
            response.Entries.Count != response.EntryCount)
        {
            Invalid(
                CapturePackageFailureKind.AggregateMismatch,
                "Capture response entry count does not match its bounded entry array.");
        }
        if (response.Entries.Any(static entry =>
                entry.ValueKind != JsonValueKind.Object))
        {
            Invalid(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture response entries must be JSON objects.");
        }
        foreach (var entry in response.Entries)
        {
            ValidateJsonElementProperties(
                entry,
                "capture response entry");
        }

        ValidateZeroResponseSemantics(
            response.PageIndex,
            response.ProviderReportedTotalPages,
            response.ProviderReportedTotalEntries,
            response.EntryCount,
            "response");
        return response;
    }

    public static byte[] SerializeResponse(
        CaptureResponseArtifact response) =>
        TierZeroCanonicalJson.Serialize(ValidateResponse(response));

    public static CaptureResponseArtifact DeserializeResponse(
        ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length <= 0 ||
            bytes.Length >
            CapturePackageFormat.MaximumResponseRecordBytes)
        {
            Invalid(
                CapturePackageFailureKind.RecordLimitExceeded,
                "Capture response bytes are outside the supported bounds.");
        }
        PreflightJsonStructure(
            bytes,
            "entries",
            CapturePackageFormat.MaximumResponseEntries,
            "capture response entries");

        CaptureResponseArtifact parsed;
        try
        {
            parsed =
                TierZeroCanonicalJson.Deserialize<CaptureResponseArtifact>(
                    bytes);
        }
        catch (JsonException exception)
        {
            throw new CapturePackageException(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture response is invalid JSON.",
                exception);
        }

        var validated = ValidateResponse(parsed);
        if (!bytes.SequenceEqual(
                TierZeroCanonicalJson.Serialize(validated)))
        {
            Invalid(
                CapturePackageFailureKind.NonCanonicalJson,
                "Capture response bytes are not canonical.");
        }
        return validated;
    }

    public static CapturePackageDefinition ValidateDefinition(
        CapturePackageDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Catalog is null ||
            definition.EnabledSoloInstruments is null ||
            definition.EnabledBandTypes is null ||
            definition.Requests is null ||
            definition.Scopes is null)
        {
            Invalid(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture package definition is missing required metadata.");
        }

        RequireSafeText(definition.CaptureId, "capture ID");
        ValidateKnownStatus(definition.Status);
        if (definition.Status != CapturePackageStatus.Complete)
        {
            Invalid(
                CapturePackageFailureKind.IncompleteCapture,
                "Only a complete capture package can be sealed.");
        }

        var catalog = ValidateCatalog(definition.Catalog);
        var enabledSolo = ValidateEnabledTypes(
            definition.EnabledSoloInstruments,
            CapturePackageFormat.SoloInstrumentOrder,
            "solo instrument");
        var enabledBands = ValidateEnabledTypes(
            definition.EnabledBandTypes,
            CapturePackageFormat.BandTypeOrder,
            "band type");
        if (enabledSolo.Count + enabledBands.Count == 0)
        {
            Invalid(
                CapturePackageFailureKind.MissingScope,
                "Capture package requires at least one enabled solo instrument or band type.");
        }

        if (definition.CaptureStartedAtUtc == default ||
            definition.CaptureCompletedAtUtc == default)
        {
            Invalid(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture start and completion timestamps are required.");
        }
        var startedAtUtc =
            definition.CaptureStartedAtUtc.ToUniversalTime();
        var completedAtUtc =
            definition.CaptureCompletedAtUtc.ToUniversalTime();
        if (completedAtUtc < startedAtUtc)
        {
            Invalid(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture completion cannot precede capture start.");
        }

        ValidateRecordCount(
            definition.TotalScopeCount,
            CapturePackageFormat.MaximumScopeRecords,
            "total scope");
        RequireNonNegative(
            definition.TotalEntryCount,
            "total entry count");
        if (definition.TotalPageCount <= 0 ||
            definition.TotalRequestCount <= 0 ||
            definition.TotalResponseBytes <= 0 ||
            definition.ResponseShardCount <= 0 ||
            definition.Requests.Count <= 0)
        {
            Invalid(
                CapturePackageFailureKind.IncompleteCapture,
                "v1 does not seal zero-request or all-unsupported capture packages.");
        }
        ValidateRecordCount(
            definition.TotalPageCount,
            CapturePackageFormat.MaximumRequestRecords,
            "total captured page");
        if (definition.ResponseShardCount >
            CapturePackageFormat.MaximumResponseShards)
        {
            Invalid(
                CapturePackageFailureKind.RecordLimitExceeded,
                "Capture response shard count is outside the supported bounds.");
        }
        ValidateRecordCount(
            definition.Scopes.Count,
            CapturePackageFormat.MaximumScopeRecords,
            "scope");
        ValidateRecordCount(
            definition.Requests.Count,
            CapturePackageFormat.MaximumRequestRecords,
            "request");

        var scopes = definition.Scopes.ToArray();
        ValidateOrdinals(
            scopes,
            static scope => scope?.Ordinal ?? -1,
            "scope");
        foreach (var scope in scopes)
        {
            if (scope is null)
            {
                Invalid(
                    CapturePackageFailureKind.InvalidMetadata,
                    "Capture scope descriptors cannot be null.");
            }
            ValidateScope(scope, enabledSolo, enabledBands);
        }
        ValidateScopeUniverse(
            scopes,
            catalog,
            enabledSolo,
            enabledBands);

        var requests = definition.Requests.ToArray();
        ValidateOrdinals(
            requests,
            static request => request?.Ordinal ?? -1,
            "request");
        foreach (var request in requests)
        {
            if (request is null)
            {
                Invalid(
                    CapturePackageFailureKind.InvalidMetadata,
                    "Capture request descriptors cannot be null.");
            }
            ValidateRequest(request, enabledSolo, enabledBands);
        }
        ValidateRequestsAgainstScopes(requests, scopes);
        var shardLayouts = BuildResponseShardLayouts(requests);
        if (shardLayouts.Count != definition.ResponseShardCount)
        {
            Invalid(
                CapturePackageFailureKind.AggregateMismatch,
                "Capture response shard count does not match request member references.");
        }
        ValidateAggregates(definition, requests, scopes);

        return definition with
        {
            Catalog = catalog,
            EnabledSoloInstruments = enabledSolo,
            EnabledBandTypes = enabledBands,
            CaptureStartedAtUtc = startedAtUtc,
            CaptureCompletedAtUtc = completedAtUtc,
            Requests = requests,
            Scopes = scopes,
        };
    }

    public static CapturePackageManifest ValidateManifest(
        CapturePackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var normalized = NormalizeManifest(manifest);
        if (!TierZeroCanonicalJson.IsSha256(
                normalized.ManifestRootHash))
        {
            Invalid(
                CapturePackageFailureKind.InvalidHash,
                "Capture manifest root hash must be a canonical SHA-256 value.");
        }

        var expected = ComputeManifestRootHash(normalized);
        if (!string.Equals(
                expected,
                normalized.ManifestRootHash,
                StringComparison.Ordinal))
        {
            Invalid(
                CapturePackageFailureKind.InvalidHash,
                "Capture manifest root hash does not match its canonical metadata.");
        }
        return normalized;
    }

    public static byte[] SerializeManifest(
        CapturePackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var normalized = NormalizeManifest(manifest);
        var rootHash = ComputeManifestRootHash(normalized);
        if (normalized.ManifestRootHash is not null &&
            !string.Equals(
                normalized.ManifestRootHash,
                rootHash,
                StringComparison.Ordinal))
        {
            Invalid(
                CapturePackageFailureKind.InvalidHash,
                "Capture manifest root hash does not match its canonical metadata.");
        }

        return TierZeroCanonicalJson.Serialize(
            normalized with { ManifestRootHash = rootHash });
    }

    public static CapturePackageManifest DeserializeManifest(
        ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length <= 0 ||
            bytes.Length > CapturePackageFormat.MaximumManifestBytes)
        {
            Invalid(
                CapturePackageFailureKind.RecordLimitExceeded,
                "Capture manifest bytes are outside the supported bounds.");
        }

        CapturePackageManifest parsed;
        try
        {
            parsed =
                TierZeroCanonicalJson.Deserialize<CapturePackageManifest>(
                    bytes);
        }
        catch (JsonException exception)
        {
            throw new CapturePackageException(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture manifest is invalid JSON.",
                exception);
        }

        var validated = ValidateManifest(parsed);
        if (!bytes.SequenceEqual(
                TierZeroCanonicalJson.Serialize(validated)))
        {
            Invalid(
                CapturePackageFailureKind.NonCanonicalJson,
                "Capture manifest bytes are not canonical.");
        }
        return validated;
    }

    public static string ComputeScopeContentSha256(
        IEnumerable<CaptureRequestDescriptor> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        var count = 0;
        foreach (var request in requests)
        {
            if (request is null)
            {
                Invalid(
                    CapturePackageFailureKind.InvalidMetadata,
                    "Capture request descriptors cannot be null.");
            }
            if (++count > CapturePackageFormat.MaximumRequestRecords)
            {
                Invalid(
                    CapturePackageFailureKind.RecordLimitExceeded,
                    "Capture scope request count exceeds the supported bound.");
            }
            AppendScopeRequestFingerprint(hash, request);
        }
        return Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant();
    }

    internal static string ComputeScopeContentSha256(
        IReadOnlyList<CaptureRequestDescriptor> requests,
        int startIndex,
        int count)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (startIndex < 0 ||
            count < 0 ||
            startIndex > requests.Count - count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startIndex),
                "Capture scope request range is outside the descriptor list.");
        }

        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        for (var index = startIndex;
             index < startIndex + count;
             index++)
        {
            AppendScopeRequestFingerprint(
                hash,
                requests[index]);
        }
        return Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant();
    }

    internal static CaptureJsonLinesMeasurement MeasureRequestPlan(
        CapturePackageDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return CapturePackageJsonLines.Measure(
            definition.Requests,
            CapturePackageFormat.MaximumRequestRecords,
            CapturePackageFormat.MaximumDescriptorSetBytes,
            CapturePackageFormat.MaximumRequestDescriptorBytes,
            "Capture request plan");
    }

    internal static CaptureJsonLinesMeasurement
        MeasureScopeCompleteness(
            CapturePackageDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return CapturePackageJsonLines.Measure(
            definition.Scopes,
            CapturePackageFormat.MaximumScopeRecords,
            CapturePackageFormat.MaximumDescriptorSetBytes,
            CapturePackageFormat.MaximumScopeDescriptorBytes,
            "Capture scope completeness");
    }

    internal static Stream OpenRequestPlanStream(
        IReadOnlyList<CaptureRequestDescriptor> requests) =>
        CapturePackageJsonLines.OpenReadStream(
            requests,
            CapturePackageFormat.MaximumRequestDescriptorBytes,
            "Capture request plan");

    internal static Stream OpenScopeCompletenessStream(
        IReadOnlyList<CaptureScopeDescriptor> scopes) =>
        CapturePackageJsonLines.OpenReadStream(
            scopes,
            CapturePackageFormat.MaximumScopeDescriptorBytes,
            "Capture scope completeness");

    internal static CapturePackageManifest CreateManifest(
        TierZeroPackageDraft envelope,
        CapturePackageDefinition definition,
        CaptureCatalogIdentity catalog,
        CaptureDescriptorSetReference requestPlan,
        CaptureDescriptorSetReference scopeCompleteness)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(requestPlan);
        ArgumentNullException.ThrowIfNull(scopeCompleteness);
        var normalizedEnvelope = NormalizeEnvelope(envelope);
        var normalizedDefinition = ValidateDefinition(definition);
        var normalizedCatalog = ValidateCatalogIdentity(catalog);
        var expectedCatalog =
            CreateCatalogIdentity(normalizedDefinition.Catalog);
        if (!CanonicalEquals(normalizedCatalog, expectedCatalog))
        {
            Invalid(
                CapturePackageFailureKind.ArtifactMismatch,
                "Capture catalog identity does not match the canonical catalog definition.");
        }
        var manifest = new CapturePackageManifest(
            CapturePackageFormat.FormatId,
            CapturePackageFormat.Version,
            normalizedDefinition.CaptureId,
            CapturePackageFormat.ProviderId,
            TierZeroEvidenceFormat.FormatId,
            TierZeroEvidenceFormat.ManifestVersion,
            normalizedEnvelope.PackageId,
            normalizedEnvelope.Attempt,
            normalizedEnvelope.ProducerIdentity,
            normalizedEnvelope.CreatedAtUtc,
            normalizedEnvelope.Source,
            normalizedEnvelope.Build,
            normalizedEnvelope.Database,
            normalizedEnvelope.Configuration,
            normalizedEnvelope.ParentRootHashes,
            normalizedCatalog,
            normalizedDefinition.EnabledSoloInstruments,
            normalizedDefinition.EnabledBandTypes,
            normalizedDefinition.CaptureStartedAtUtc,
            normalizedDefinition.CaptureCompletedAtUtc,
            normalizedDefinition.TotalScopeCount,
            normalizedDefinition.TotalPageCount,
            normalizedDefinition.TotalEntryCount,
            normalizedDefinition.TotalRequestCount,
            normalizedDefinition.TotalResponseBytes,
            normalizedDefinition.ResponseShardCount,
            requestPlan,
            scopeCompleteness,
            normalizedDefinition.Status,
            null);
        return NormalizeManifest(manifest);
    }

    internal static void ValidateEnvelope(
        CapturePackageManifest manifest,
        TierZeroEvidenceManifest envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var normalizedManifest = ValidateManifest(manifest);
        TierZeroEvidenceManifest normalizedEnvelope;
        try
        {
            normalizedEnvelope =
                TierZeroPackageModel.NormalizeManifest(envelope);
        }
        catch (TierZeroPackageException exception)
        {
            throw new CapturePackageException(
                CapturePackageFailureKind.EnvelopeMismatch,
                "Tier-0 capture envelope metadata is invalid.",
                exception);
        }

        if (normalizedEnvelope.Status != TierZeroPackageStatus.Sealed ||
            !string.Equals(
                normalizedManifest.EnvelopeFormatId,
                normalizedEnvelope.FormatId,
                StringComparison.Ordinal) ||
            normalizedManifest.EnvelopeManifestVersion !=
                normalizedEnvelope.ManifestVersion ||
            !string.Equals(
                normalizedManifest.EnvelopePackageId,
                normalizedEnvelope.PackageId,
                StringComparison.Ordinal) ||
            normalizedManifest.EnvelopeAttempt !=
                normalizedEnvelope.Attempt ||
            !string.Equals(
                normalizedManifest.ProducerIdentity,
                normalizedEnvelope.ProducerIdentity,
                StringComparison.Ordinal) ||
            normalizedManifest.EnvelopeCreatedAtUtc !=
                normalizedEnvelope.CreatedAtUtc ||
            !CanonicalEquals(
                normalizedManifest.Source,
                normalizedEnvelope.Source) ||
            !CanonicalEquals(
                normalizedManifest.Build,
                normalizedEnvelope.Build) ||
            !CanonicalEquals(
                normalizedManifest.Database,
                normalizedEnvelope.Database) ||
            !CanonicalEquals(
                normalizedManifest.ScrapeConfiguration,
                normalizedEnvelope.Configuration) ||
            !CanonicalEquals(
                normalizedManifest.ParentRootHashes,
                normalizedEnvelope.ParentRootHashes) ||
            normalizedManifest.CaptureCompletedAtUtc >
                normalizedEnvelope.SealedAtUtc)
        {
            Invalid(
                CapturePackageFailureKind.EnvelopeMismatch,
                "Capture manifest identity does not match its sealed Tier-0 envelope.");
        }
    }

    internal static CapturePackageDefinition DefinitionFrom(
        CapturePackageManifest manifest,
        CaptureCatalogArtifact catalog,
        IReadOnlyList<CaptureRequestDescriptor> requests,
        IReadOnlyList<CaptureScopeDescriptor> scopes) =>
        new(
            manifest.CaptureId,
            catalog,
            manifest.EnabledSoloInstruments,
            manifest.EnabledBandTypes,
            manifest.CaptureStartedAtUtc,
            manifest.CaptureCompletedAtUtc,
            manifest.TotalScopeCount,
            manifest.TotalPageCount,
            manifest.TotalEntryCount,
            manifest.TotalRequestCount,
            manifest.TotalResponseBytes,
            manifest.ResponseShardCount,
            requests,
            scopes,
            manifest.Status);

    internal static CaptureDescriptorSetReference CreateReference(
        string path,
        CaptureJsonLinesMeasurement measurement) =>
        new(
            path,
            CapturePackageFormat.DescriptorSchemaVersion,
            measurement.RecordCount,
            measurement.Bytes,
            measurement.Sha256);

    internal static TierZeroArtifactDescriptor ExpectedCatalogArtifact(
        CaptureCatalogIdentity catalog) =>
        new(
            CapturePackageFormat.CatalogOwner,
            CapturePackageFormat.CatalogPath,
            CapturePackageFormat.JsonMediaType,
            catalog.SchemaVersion,
            catalog.SongCount,
            [],
            catalog.Bytes,
            catalog.Bytes,
            catalog.ContentSha256);

    internal static TierZeroArtifactDescriptor ExpectedMetadataArtifact(
        string owner,
        string path,
        string mediaType,
        CaptureJsonLinesMeasurement measurement) =>
        new(
            owner,
            path,
            mediaType,
            CapturePackageFormat.DescriptorSchemaVersion,
            measurement.RecordCount,
            [],
            measurement.Bytes,
            measurement.Bytes,
            measurement.Sha256);

    internal static TierZeroArtifactDescriptor ExpectedMetadataArtifact(
        string owner,
        string path,
        string mediaType,
        int rowCount,
        ReadOnlySpan<byte> bytes) =>
        new(
            owner,
            path,
            mediaType,
            CapturePackageFormat.DescriptorSchemaVersion,
            rowCount,
            [],
            bytes.Length,
            bytes.Length,
            TierZeroCanonicalJson.Sha256Hex(bytes));

    internal static async Task ValidateContentArtifactForWriteAsync(
        TierZeroArtifactRegistration registration,
        byte[] content,
        TierZeroPackageDraft draft,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                registration.Path,
                TierZeroPackagePath.Normalize(registration.Path),
                StringComparison.Ordinal))
        {
            Invalid(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture artifact paths must already be canonical.");
        }
        if (content.Length <= 0)
        {
            Invalid(
                CapturePackageFailureKind.ArtifactMismatch,
                "Capture content artifacts cannot be empty.");
        }

        if (string.Equals(
                registration.Path,
                CapturePackageFormat.CatalogPath,
                StringComparison.Ordinal))
        {
            if (content.Length >
                CapturePackageFormat.MaximumCatalogBytes)
            {
                Invalid(
                    CapturePackageFailureKind.RecordLimitExceeded,
                    "Capture catalog exceeds its byte limit.");
            }
            var catalog = DeserializeCatalog(content);
            var identity = CreateCatalogIdentity(catalog, content);
            ValidateRegistration(
                registration,
                CapturePackageFormat.CatalogOwner,
                CapturePackageFormat.JsonMediaType,
                CapturePackageFormat.CatalogSchemaVersion,
                catalog.SongCount,
                content.Length,
                "catalog");
            if (!string.Equals(
                    identity.ContentSha256,
                    draft.Source.Catalog.ContentSha256,
                    StringComparison.Ordinal))
            {
                Invalid(
                    CapturePackageFailureKind.EnvelopeMismatch,
                    "Capture catalog hash does not match the Tier-0 source catalog hash.");
            }
            return;
        }

        if (!CapturePackageFormat.TryParseResponseShardPath(
                registration.Path,
                out _))
        {
            Invalid(
                CapturePackageFailureKind.ArtifactMismatch,
                $"Capture content artifact path '{registration.Path}' is not part of the v1 closed set.");
        }
        if (content.Length >
            CapturePackageFormat.MaximumResponseShardBytes)
        {
            Invalid(
                CapturePackageFailureKind.RecordLimitExceeded,
                "Capture response shard exceeds its byte limit.");
        }
        if (registration.RowCount <= 0 ||
            registration.RowCount >
            CapturePackageFormat.MaximumRequestRecords ||
            registration.RowCount > int.MaxValue)
        {
            Invalid(
                CapturePackageFailureKind.RecordLimitExceeded,
                "Capture response shard member count is outside the supported bounds.");
        }
        ValidateRegistration(
            registration,
            CapturePackageFormat.ResponseShardOwner,
            CapturePackageFormat.JsonLinesMediaType,
            CapturePackageFormat.ResponseSchemaVersion,
            registration.RowCount,
            content.Length,
            "response shard");

        var previousRequestOrdinal = -1;
        await using var stream = new MemoryStream(
            content,
            writable: false);
        await CapturePackageJsonLines.ReadAsync<CaptureResponseArtifact>(
            stream,
            content.Length,
            checked((int)registration.RowCount),
            CapturePackageFormat.MaximumRequestRecords,
            CapturePackageFormat.MaximumResponseShardBytes,
            CapturePackageFormat.MaximumResponseRecordBytes,
            "Capture response shard",
            PreflightResponseRecord,
            (response, _, _, _, _) =>
            {
                var normalized = ValidateResponse(response);
                if (normalized.RequestOrdinal <=
                    previousRequestOrdinal)
                {
                    Invalid(
                        CapturePackageFailureKind.NonCanonicalOrder,
                        "Capture response shard request ordinals must increase strictly.");
                }
                previousRequestOrdinal =
                    normalized.RequestOrdinal;
            },
            cancellationToken);
    }

    internal static void EnsureContentAdditionAllowed(
        IReadOnlyList<TierZeroArtifactDescriptor> artifacts)
    {
        if (artifacts.Any(static artifact =>
                IsCaptureMetadataPath(artifact.Path)))
        {
            Invalid(
                CapturePackageFailureKind.ArtifactMismatch,
                "Capture content cannot change after canonical capture metadata has started.");
        }
    }

    internal static async Task ValidatePackageArtifactsAsync(
        string rootPath,
        TierZeroPackageDraft draft,
        CapturePackageDefinition definition,
        CaptureCatalogIdentity catalogIdentity,
        CapturePackageManifest manifest,
        CaptureJsonLinesMeasurement requestMeasurement,
        CaptureJsonLinesMeasurement scopeMeasurement,
        IReadOnlyList<TierZeroArtifactDescriptor> artifacts,
        CancellationToken cancellationToken)
    {
        var normalized = ValidateDefinition(definition);
        ValidateContentArtifactDescriptors(
            normalized,
            catalogIdentity,
            artifacts,
            requireMetadata: true);
        var byPath = ToArtifactDictionary(artifacts);

        var catalogArtifact = RequireArtifact(
            byPath,
            CapturePackageFormat.CatalogPath);
        var catalogBytes = await ReadArtifactBytesAsync(
            rootPath,
            catalogArtifact,
            CapturePackageFormat.MaximumCatalogBytes,
            cancellationToken);
        var catalog = DeserializeCatalog(catalogBytes);
        if (!CanonicalEquals(catalog, normalized.Catalog) ||
            !CanonicalEquals(
                CreateCatalogIdentity(catalog, catalogBytes),
                catalogIdentity))
        {
            Invalid(
                CapturePackageFailureKind.ArtifactMismatch,
                "Capture catalog bytes do not match the sealed definition.");
        }

        await ValidateDescriptorArtifactAgainstRowsAsync(
            rootPath,
            RequireArtifact(
                byPath,
                CapturePackageFormat.RequestPlanPath),
            requestMeasurement,
            CapturePackageFormat.MaximumRequestRecords,
            CapturePackageFormat.MaximumRequestDescriptorBytes,
            "Capture request plan",
            normalized.Requests,
            cancellationToken);
        await ValidateDescriptorArtifactAgainstRowsAsync(
            rootPath,
            RequireArtifact(
                byPath,
                CapturePackageFormat.ScopeCompletenessPath),
            scopeMeasurement,
            CapturePackageFormat.MaximumScopeRecords,
            CapturePackageFormat.MaximumScopeDescriptorBytes,
            "Capture scope completeness",
            normalized.Scopes,
            cancellationToken);

        var manifestArtifact = RequireArtifact(
            byPath,
            CapturePackageFormat.ManifestPath);
        var manifestBytes = await ReadArtifactBytesAsync(
            rootPath,
            manifestArtifact,
            CapturePackageFormat.MaximumManifestBytes,
            cancellationToken);
        var parsedManifest = DeserializeManifest(manifestBytes);
        if (!CanonicalEquals(parsedManifest, manifest))
        {
            Invalid(
                CapturePackageFailureKind.ArtifactMismatch,
                "Capture manifest artifact does not match the sealing manifest.");
        }
        RequireArtifact(
            byPath,
            ExpectedMetadataArtifact(
                CapturePackageFormat.ManifestOwner,
                CapturePackageFormat.ManifestPath,
                CapturePackageFormat.JsonMediaType,
                1,
                manifestBytes));

        var expectedManifest = DeserializeManifest(
            SerializeManifest(
                CreateManifest(
                    draft,
                    normalized,
                    catalogIdentity,
                    CreateReference(
                        CapturePackageFormat.RequestPlanPath,
                        requestMeasurement),
                    CreateReference(
                        CapturePackageFormat.ScopeCompletenessPath,
                        scopeMeasurement))));
        if (!CanonicalEquals(expectedManifest, manifest))
        {
            Invalid(
                CapturePackageFailureKind.EnvelopeMismatch,
                "Capture manifest no longer matches the refreshed Tier-0 package state.");
        }

        await ValidateResponseShardsAsync(
            rootPath,
            normalized,
            byPath,
            cancellationToken);
    }

    internal static void ValidateContentArtifactDescriptors(
        CapturePackageDefinition definition,
        CaptureCatalogIdentity catalog,
        IReadOnlyList<TierZeroArtifactDescriptor> artifacts,
        bool requireMetadata)
    {
        var normalized = ValidateDefinition(definition);
        var byPath = ToArtifactDictionary(artifacts);
        RequireArtifact(
            byPath,
            ExpectedCatalogArtifact(catalog));

        var layouts = BuildResponseShardLayouts(
            normalized.Requests);
        foreach (var layout in layouts)
        {
            var artifact = RequireArtifact(byPath, layout.Path);
            if (!string.Equals(
                    artifact.LogicalOwner,
                    CapturePackageFormat.ResponseShardOwner,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    artifact.MediaType,
                    CapturePackageFormat.JsonLinesMediaType,
                    StringComparison.Ordinal) ||
                artifact.SchemaVersion !=
                    CapturePackageFormat.ResponseSchemaVersion ||
                artifact.RowCount != layout.RecordCount ||
                artifact.CompressedBytes != layout.Bytes ||
                artifact.UncompressedBytes != layout.Bytes ||
                !TierZeroCanonicalJson.IsSha256(artifact.Sha256) ||
                artifact.Ranges.Count != 0)
            {
                Invalid(
                    CapturePackageFailureKind.ArtifactMismatch,
                    $"Capture response shard '{layout.Path}' does not match its member layout.");
            }
        }

        var allowed = layouts
            .Select(static layout => layout.Path)
            .Append(CapturePackageFormat.CatalogPath)
            .Append(CapturePackageFormat.RequestPlanPath)
            .Append(CapturePackageFormat.ScopeCompletenessPath)
            .Append(CapturePackageFormat.ManifestPath)
            .ToHashSet(StringComparer.Ordinal);
        var extra = artifacts.FirstOrDefault(
            artifact => !allowed.Contains(artifact.Path));
        if (extra is not null)
        {
            Invalid(
                CapturePackageFailureKind.ArtifactMismatch,
                $"Capture envelope contains undeclared artifact '{extra.Path}'.");
        }

        var expectedContentCount = checked(layouts.Count + 1);
        var expectedTotalCount = requireMetadata
            ? checked(expectedContentCount + 3)
            : expectedContentCount;
        if (requireMetadata)
        {
            foreach (var metadataPath in new[]
                     {
                         CapturePackageFormat.RequestPlanPath,
                         CapturePackageFormat.ScopeCompletenessPath,
                         CapturePackageFormat.ManifestPath,
                     })
            {
                _ = RequireArtifact(byPath, metadataPath);
            }
        }
        if (artifacts.Count != expectedTotalCount)
        {
            Invalid(
                CapturePackageFailureKind.ArtifactMismatch,
                "Capture envelope artifact count does not match its closed v1 artifact set.");
        }
    }

    internal static void PreflightEnvelopeArtifacts(
        CapturePackageManifest manifest,
        IReadOnlyList<TierZeroArtifactDescriptor> artifacts)
    {
        var normalizedManifest = ValidateManifest(manifest);
        var byPath = ToArtifactDictionary(artifacts);
        RequireArtifact(
            byPath,
            ExpectedCatalogArtifact(normalizedManifest.Catalog));

        var manifestArtifact = RequireArtifact(
            byPath,
            CapturePackageFormat.ManifestPath);
        ValidateMetadataDescriptor(
            manifestArtifact,
            CapturePackageFormat.ManifestOwner,
            CapturePackageFormat.JsonMediaType,
            CapturePackageFormat.DescriptorSchemaVersion,
            expectedRowCount: 1,
            CapturePackageFormat.MaximumManifestBytes,
            "capture manifest");
        ValidateReferencedMetadataDescriptor(
            RequireArtifact(
                byPath,
                CapturePackageFormat.RequestPlanPath),
            normalizedManifest.RequestPlan,
            CapturePackageFormat.RequestPlanOwner,
            CapturePackageFormat.JsonLinesMediaType,
            "capture request plan");
        ValidateReferencedMetadataDescriptor(
            RequireArtifact(
                byPath,
                CapturePackageFormat.ScopeCompletenessPath),
            normalizedManifest.ScopeCompleteness,
            CapturePackageFormat.ScopeCompletenessOwner,
            CapturePackageFormat.JsonLinesMediaType,
            "capture scope completeness");

        for (var ordinal = 0;
             ordinal < normalizedManifest.ResponseShardCount;
             ordinal++)
        {
            var path = CapturePackageFormat.ResponseShardPath(
                ordinal);
            var artifact = RequireArtifact(byPath, path);
            ValidateMetadataDescriptor(
                artifact,
                CapturePackageFormat.ResponseShardOwner,
                CapturePackageFormat.JsonLinesMediaType,
                CapturePackageFormat.ResponseSchemaVersion,
                expectedRowCount: null,
                CapturePackageFormat.MaximumResponseShardBytes,
                $"capture response shard '{path}'");
            if (artifact.RowCount <= 0 ||
                artifact.RowCount >
                CapturePackageFormat.MaximumRequestRecords)
            {
                Invalid(
                    CapturePackageFailureKind.RecordLimitExceeded,
                    $"Capture response shard '{path}' row count is outside the supported bounds.");
            }
        }

        var expectedArtifactCount = checked(
            normalizedManifest.ResponseShardCount + 4);
        if (artifacts.Count != expectedArtifactCount)
        {
            Invalid(
                CapturePackageFailureKind.ArtifactMismatch,
                "Capture envelope artifact count does not match its bounded v1 closed set.");
        }
    }

    internal static TierZeroArtifactDescriptor RequireArtifact(
        IReadOnlyDictionary<string, TierZeroArtifactDescriptor> byPath,
        string path) =>
        byPath.TryGetValue(path, out var artifact)
            ? artifact
            : throw new CapturePackageException(
                CapturePackageFailureKind.ArtifactMismatch,
                $"Capture envelope is missing artifact '{path}'.");

    internal static void RequireArtifact(
        IReadOnlyDictionary<string, TierZeroArtifactDescriptor> byPath,
        TierZeroArtifactDescriptor expected)
    {
        if (!byPath.TryGetValue(expected.Path, out var actual) ||
            !CanonicalEquals(actual, expected))
        {
            Invalid(
                CapturePackageFailureKind.ArtifactMismatch,
                $"Capture artifact '{expected.Path}' does not match its descriptor.");
        }
    }

    internal static async Task<byte[]> ReadArtifactBytesAsync(
        string rootPath,
        TierZeroArtifactDescriptor artifact,
        long maximumBytes,
        CancellationToken cancellationToken,
        TierZeroFileSnapshot? expectedSnapshot = null)
    {
        ValidateReadableArtifact(
            artifact,
            maximumBytes);
        var path = ResolveArtifactPath(rootPath, artifact.Path);
        var snapshot = TierZeroRegularFile.Inspect(path);
        if (expectedSnapshot is not null &&
            snapshot != expectedSnapshot)
        {
            Invalid(
                CapturePackageFailureKind.ArtifactMismatch,
                $"Capture artifact '{artifact.Path}' changed after package preflight.");
        }
        if (snapshot.Length != artifact.CompressedBytes)
        {
            Invalid(
                CapturePackageFailureKind.ArtifactMismatch,
                $"Capture artifact '{artifact.Path}' size changed.");
        }
        var bytes = await TierZeroRegularFile.ReadAllBytesAsync(
            path,
            snapshot,
            maximumBytes,
            cancellationToken);
        if (!string.Equals(
                TierZeroCanonicalJson.Sha256Hex(bytes),
                artifact.Sha256,
                StringComparison.Ordinal))
        {
            Invalid(
                CapturePackageFailureKind.ArtifactMismatch,
                $"Capture artifact '{artifact.Path}' hash changed.");
        }
        return bytes;
    }

    internal static async Task<CaptureJsonLinesMeasurement>
        ReadJsonLinesArtifactAsync<T>(
            string rootPath,
            TierZeroArtifactDescriptor artifact,
            int expectedCount,
            long expectedBytes,
            int maximumRecords,
            long maximumBytes,
            int maximumRecordBytes,
            string description,
            Action<ReadOnlyMemory<byte>>? preDeserialize,
            Action<T, int, long, int, string> onRecord,
            CancellationToken cancellationToken,
            TierZeroFileSnapshot? expectedSnapshot = null)
    {
        ValidateReadableArtifact(artifact, maximumBytes);
        if (artifact.RowCount != expectedCount ||
            artifact.CompressedBytes != expectedBytes ||
            artifact.UncompressedBytes != expectedBytes)
        {
            Invalid(
                CapturePackageFailureKind.ArtifactMismatch,
                $"{description} Tier-0 descriptor does not match its capture reference.");
        }

        var path = ResolveArtifactPath(rootPath, artifact.Path);
        var snapshot = TierZeroRegularFile.Inspect(path);
        if (expectedSnapshot is not null &&
            snapshot != expectedSnapshot)
        {
            Invalid(
                CapturePackageFailureKind.ArtifactMismatch,
                $"{description} changed after package preflight.");
        }
        if (snapshot.Length != artifact.CompressedBytes)
        {
            Invalid(
                CapturePackageFailureKind.ArtifactMismatch,
                $"{description} size changed.");
        }

        await using var opened = TierZeroRegularFile.OpenRead(
            path,
            snapshot);
        var measurement =
            await CapturePackageJsonLines.ReadAsync(
                opened.Stream,
                expectedBytes,
                expectedCount,
                maximumRecords,
                maximumBytes,
                maximumRecordBytes,
                description,
                preDeserialize,
                onRecord,
                cancellationToken);
        opened.ValidateUnchanged();
        if (!string.Equals(
                measurement.Sha256,
                artifact.Sha256,
                StringComparison.Ordinal))
        {
            Invalid(
                CapturePackageFailureKind.ArtifactMismatch,
                $"{description} hash changed.");
        }
        return measurement;
    }

    internal static void ValidateReference(
        CaptureDescriptorSetReference reference,
        TierZeroArtifactDescriptor artifact)
    {
        if (!string.Equals(
                reference.Path,
                artifact.Path,
                StringComparison.Ordinal) ||
            reference.SchemaVersion != artifact.SchemaVersion ||
            reference.RecordCount != artifact.RowCount ||
            reference.Bytes != artifact.CompressedBytes ||
            reference.Bytes != artifact.UncompressedBytes ||
            !string.Equals(
                reference.Sha256,
                artifact.Sha256,
                StringComparison.Ordinal))
        {
            Invalid(
                CapturePackageFailureKind.ArtifactMismatch,
                $"Capture descriptor set '{reference.Path}' does not match its Tier-0 artifact.");
        }
    }

    private static void ValidateReferencedMetadataDescriptor(
        TierZeroArtifactDescriptor artifact,
        CaptureDescriptorSetReference reference,
        string expectedOwner,
        string expectedMediaType,
        string description)
    {
        ValidateReference(reference, artifact);
        ValidateMetadataDescriptor(
            artifact,
            expectedOwner,
            expectedMediaType,
            CapturePackageFormat.DescriptorSchemaVersion,
            reference.RecordCount,
            CapturePackageFormat.MaximumDescriptorSetBytes,
            description);
    }

    private static void ValidateMetadataDescriptor(
        TierZeroArtifactDescriptor artifact,
        string expectedOwner,
        string expectedMediaType,
        int expectedSchemaVersion,
        int? expectedRowCount,
        long maximumBytes,
        string description)
    {
        if (!string.Equals(
                artifact.LogicalOwner,
                expectedOwner,
                StringComparison.Ordinal) ||
            !string.Equals(
                artifact.MediaType,
                expectedMediaType,
                StringComparison.Ordinal) ||
            artifact.SchemaVersion != expectedSchemaVersion ||
            expectedRowCount.HasValue &&
                artifact.RowCount != expectedRowCount.Value ||
            artifact.CompressedBytes <= 0 ||
            artifact.CompressedBytes > maximumBytes ||
            artifact.CompressedBytes != artifact.UncompressedBytes ||
            !TierZeroCanonicalJson.IsSha256(artifact.Sha256) ||
            artifact.Ranges.Count != 0)
        {
            Invalid(
                CapturePackageFailureKind.ArtifactMismatch,
                $"{description} Tier-0 descriptor is not canonical.");
        }
    }

    internal static bool IsCaptureMetadataPath(string path) =>
        string.Equals(
            path,
            CapturePackageFormat.ManifestPath,
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            path,
            CapturePackageFormat.RequestPlanPath,
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            path,
            CapturePackageFormat.ScopeCompletenessPath,
            StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<CaptureResponseShardLayout>
        BuildResponseShardLayouts(
            IReadOnlyList<CaptureRequestDescriptor> requests)
    {
        var layouts = new List<CaptureResponseShardLayout>();
        var currentShardOrdinal = -1;
        var currentPath = "";
        var currentStart = 0;
        var currentCount = 0;
        long expectedOffset = 0;

        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            if (!CapturePackageFormat.TryParseResponseShardPath(
                    request.ResponseShardPath,
                    out var shardOrdinal))
            {
                Invalid(
                    CapturePackageFailureKind.InvalidMetadata,
                    $"Capture request {request.Ordinal} response shard path is not canonical.");
            }

            if (shardOrdinal != currentShardOrdinal)
            {
                if (currentShardOrdinal >= 0)
                {
                    layouts.Add(new CaptureResponseShardLayout(
                        currentShardOrdinal,
                        currentPath,
                        currentStart,
                        currentCount,
                        expectedOffset));
                }
                if (shardOrdinal != currentShardOrdinal + 1)
                {
                    Invalid(
                        CapturePackageFailureKind.MissingOrdinal,
                        "Capture response shard ordinals must be contiguous from zero.");
                }
                currentShardOrdinal = shardOrdinal;
                currentPath = request.ResponseShardPath;
                currentStart = index;
                currentCount = 0;
                expectedOffset = 0;
            }

            if (request.ResponseOffset != expectedOffset)
            {
                Invalid(
                    CapturePackageFailureKind.AggregateMismatch,
                    $"Capture request {request.Ordinal} response member overlaps or leaves a shard gap.");
            }
            try
            {
                expectedOffset = checked(
                    expectedOffset +
                    request.ResponseLength +
                    1L);
            }
            catch (OverflowException exception)
            {
                throw new CapturePackageException(
                    CapturePackageFailureKind.AggregateMismatch,
                    "Capture response shard member offsets overflowed.",
                    exception);
            }
            if (expectedOffset >
                CapturePackageFormat.MaximumResponseShardBytes)
            {
                Invalid(
                    CapturePackageFailureKind.RecordLimitExceeded,
                    $"Capture response shard '{currentPath}' exceeds its byte limit.");
            }
            currentCount++;
        }

        if (currentShardOrdinal >= 0)
        {
            layouts.Add(new CaptureResponseShardLayout(
                currentShardOrdinal,
                currentPath,
                currentStart,
                currentCount,
                expectedOffset));
        }
        if (layouts.Count <= 0 ||
            layouts.Count >
            CapturePackageFormat.MaximumResponseShards)
        {
            Invalid(
                CapturePackageFailureKind.RecordLimitExceeded,
                "Capture response shard count is outside the supported bounds.");
        }
        return layouts;
    }

    internal static bool CanonicalEquals<T>(
        T first,
        T second) =>
        TierZeroCanonicalJson.Serialize(first).AsSpan()
            .SequenceEqual(TierZeroCanonicalJson.Serialize(second));

    internal static void PreflightResponseRecord(
        ReadOnlyMemory<byte> bytes) =>
        PreflightJsonStructure(
            bytes.Span,
            "entries",
            CapturePackageFormat.MaximumResponseEntries,
            "capture response entries");

    private static CapturePackageManifest NormalizeManifest(
        CapturePackageManifest manifest)
    {
        if (manifest.Source is null ||
            manifest.Build is null ||
            manifest.Database is null ||
            manifest.ScrapeConfiguration is null ||
            manifest.ParentRootHashes is null ||
            manifest.Catalog is null ||
            manifest.EnabledSoloInstruments is null ||
            manifest.EnabledBandTypes is null ||
            manifest.RequestPlan is null ||
            manifest.ScopeCompleteness is null)
        {
            Invalid(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture manifest is missing required metadata.");
        }
        if (!string.Equals(
                manifest.FormatId,
                CapturePackageFormat.FormatId,
                StringComparison.Ordinal) ||
            manifest.Version != CapturePackageFormat.Version ||
            !string.Equals(
                manifest.ProviderId,
                CapturePackageFormat.ProviderId,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.EnvelopeFormatId,
                TierZeroEvidenceFormat.FormatId,
                StringComparison.Ordinal) ||
            manifest.EnvelopeManifestVersion !=
                TierZeroEvidenceFormat.ManifestVersion)
        {
            Invalid(
                CapturePackageFailureKind.UnsupportedFormat,
                "Capture package or Tier-0 envelope format identity is unsupported.");
        }

        RequireSafeText(manifest.CaptureId, "capture ID");
        var envelope = NormalizeEnvelope(
            new TierZeroPackageDraft(
                manifest.EnvelopePackageId,
                manifest.Source,
                manifest.Build,
                manifest.Database,
                manifest.ScrapeConfiguration,
                TierZeroSummaryReferences.Empty,
                manifest.ParentRootHashes,
                manifest.EnvelopeAttempt,
                manifest.ProducerIdentity,
                manifest.EnvelopeCreatedAtUtc));
        ValidateKnownStatus(manifest.Status);
        if (manifest.Status != CapturePackageStatus.Complete)
        {
            Invalid(
                CapturePackageFailureKind.IncompleteCapture,
                "A sealed capture manifest must have complete status.");
        }

        var catalog = ValidateCatalogIdentity(manifest.Catalog);
        if (!string.Equals(
                catalog.ContentSha256,
                envelope.Source.Catalog.ContentSha256,
                StringComparison.Ordinal))
        {
            Invalid(
                CapturePackageFailureKind.EnvelopeMismatch,
                "Capture catalog hash does not match the Tier-0 source catalog hash.");
        }
        var enabledSolo = ValidateEnabledTypes(
            manifest.EnabledSoloInstruments,
            CapturePackageFormat.SoloInstrumentOrder,
            "solo instrument");
        var enabledBands = ValidateEnabledTypes(
            manifest.EnabledBandTypes,
            CapturePackageFormat.BandTypeOrder,
            "band type");
        if (enabledSolo.Count + enabledBands.Count == 0)
        {
            Invalid(
                CapturePackageFailureKind.MissingScope,
                "Capture manifest requires at least one enabled scope type.");
        }

        if (manifest.CaptureStartedAtUtc == default ||
            manifest.CaptureCompletedAtUtc == default)
        {
            Invalid(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture start and completion timestamps are required.");
        }
        var startedAtUtc =
            manifest.CaptureStartedAtUtc.ToUniversalTime();
        var completedAtUtc =
            manifest.CaptureCompletedAtUtc.ToUniversalTime();
        if (startedAtUtc < envelope.CreatedAtUtc ||
            completedAtUtc < startedAtUtc)
        {
            Invalid(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture timestamps are inconsistent with the Tier-0 envelope.");
        }

        ValidateRecordCount(
            manifest.TotalScopeCount,
            CapturePackageFormat.MaximumScopeRecords,
            "manifest scope");
        RequireNonNegative(
            manifest.TotalEntryCount,
            "total entry count");
        if (manifest.TotalPageCount <= 0 ||
            manifest.TotalRequestCount <= 0 ||
            manifest.TotalResponseBytes <= 0 ||
            manifest.ResponseShardCount <= 0)
        {
            Invalid(
                CapturePackageFailureKind.IncompleteCapture,
                "v1 does not seal zero-request or all-unsupported capture manifests.");
        }
        ValidateRecordCount(
            manifest.TotalPageCount,
            CapturePackageFormat.MaximumRequestRecords,
            "manifest captured page");
        if (manifest.ResponseShardCount >
            CapturePackageFormat.MaximumResponseShards)
        {
            Invalid(
                CapturePackageFailureKind.RecordLimitExceeded,
                "Capture manifest response shard count is outside the supported bounds.");
        }
        var requestPlan = ValidateReference(
            manifest.RequestPlan,
            CapturePackageFormat.RequestPlanPath,
            manifest.TotalPageCount,
            CapturePackageFormat.MaximumRequestRecords,
            CapturePackageFormat.MaximumDescriptorSetBytes,
            "request plan");
        var scopeCompleteness = ValidateReference(
            manifest.ScopeCompleteness,
            CapturePackageFormat.ScopeCompletenessPath,
            manifest.TotalScopeCount,
            CapturePackageFormat.MaximumScopeRecords,
            CapturePackageFormat.MaximumDescriptorSetBytes,
            "scope completeness");
        if (manifest.ManifestRootHash is not null &&
            !TierZeroCanonicalJson.IsSha256(
                manifest.ManifestRootHash))
        {
            Invalid(
                CapturePackageFailureKind.InvalidHash,
                "Capture manifest root hash must be a canonical SHA-256 value.");
        }

        ValidateEnvelopeStrings(envelope);
        return manifest with
        {
            EnvelopePackageId = envelope.PackageId,
            ProducerIdentity = envelope.ProducerIdentity,
            EnvelopeCreatedAtUtc = envelope.CreatedAtUtc,
            Source = envelope.Source,
            Build = envelope.Build,
            Database = envelope.Database,
            ScrapeConfiguration = envelope.Configuration,
            ParentRootHashes = envelope.ParentRootHashes,
            Catalog = catalog,
            EnabledSoloInstruments = enabledSolo,
            EnabledBandTypes = enabledBands,
            CaptureStartedAtUtc = startedAtUtc,
            CaptureCompletedAtUtc = completedAtUtc,
            RequestPlan = requestPlan,
            ScopeCompleteness = scopeCompleteness,
        };
    }

    private static CaptureCatalogIdentity ValidateCatalogIdentity(
        CaptureCatalogIdentity catalog)
    {
        if (catalog.Version <= 0 ||
            catalog.SchemaVersion !=
                CapturePackageFormat.CatalogSchemaVersion)
        {
            Invalid(
                CapturePackageFailureKind.UnsupportedFormat,
                "Capture catalog identity version or schema is unsupported.");
        }
        ValidateRecordCount(
            catalog.SongCount,
            CapturePackageFormat.MaximumCatalogSongs,
            "catalog identity song");
        if (catalog.Bytes <= 0 ||
            catalog.Bytes >
            CapturePackageFormat.MaximumCatalogBytes)
        {
            Invalid(
                CapturePackageFailureKind.RecordLimitExceeded,
                "Capture catalog identity byte count is outside the supported bounds.");
        }
        RequireSha(catalog.ContentSha256, "catalog content hash");
        return catalog;
    }

    internal static CaptureCatalogIdentity CreateCatalogIdentity(
        CaptureCatalogArtifact catalog,
        ReadOnlySpan<byte> bytes)
    {
        var normalized = ValidateCatalog(catalog);
        if (bytes.Length <= 0 ||
            bytes.Length >
            CapturePackageFormat.MaximumCatalogBytes ||
            !bytes.SequenceEqual(
                TierZeroCanonicalJson.Serialize(normalized)))
        {
            Invalid(
                CapturePackageFailureKind.NonCanonicalJson,
                "Capture catalog identity requires exact canonical catalog bytes.");
        }
        return new CaptureCatalogIdentity(
            normalized.CatalogVersion,
            normalized.SchemaVersion,
            normalized.SongCount,
            bytes.Length,
            TierZeroCanonicalJson.Sha256Hex(bytes));
    }

    private static IReadOnlyList<string> ValidateEnabledTypes(
        IReadOnlyList<string> values,
        IReadOnlyList<string> supported,
        string description)
    {
        var copy = values.ToArray();
        if (copy.Any(static value => value is null))
        {
            Invalid(
                CapturePackageFailureKind.InvalidMetadata,
                $"Enabled {description} values cannot be null.");
        }
        foreach (var value in copy)
            RequireSafeText(value, $"enabled {description}");
        if (copy.Distinct(StringComparer.Ordinal).Count() != copy.Length ||
            copy.Any(value => !supported.Contains(
                value,
                StringComparer.Ordinal)))
        {
            Invalid(
                CapturePackageFailureKind.InvalidMetadata,
                $"Enabled {description} values must be unique supported identifiers.");
        }

        var canonical = supported
            .Where(value => copy.Contains(
                value,
                StringComparer.Ordinal))
            .ToArray();
        if (!copy.SequenceEqual(canonical))
        {
            Invalid(
                CapturePackageFailureKind.NonCanonicalOrder,
                $"Enabled {description} values are not in canonical order.");
        }
        return copy;
    }

    private static void ValidateScope(
        CaptureScopeDescriptor scope,
        IReadOnlyList<string> enabledSolo,
        IReadOnlyList<string> enabledBands)
    {
        RequireSafeText(scope.SongId, "scope song ID");
        ValidateScopeType(
            scope.ScopeKind,
            scope.LeaderboardType,
            enabledSolo,
            enabledBands);
        ValidateKnownStatus(scope.Status);
        RequireNonNegative(
            scope.DeclaredPageCount,
            "declared scope page count");
        RequireNonNegative(
            scope.DeclaredEntryCount,
            "declared scope entry count");
        RequireNonNegative(
            scope.ProviderReportedTotalPages,
            "provider-reported scope page count");
        RequireNonNegative(
            scope.ProviderReportedTotalEntries,
            "provider-reported scope entry count");
        RequireNonNegative(
            scope.CapturedPageCount,
            "captured scope page count");
        RequireNonNegative(
            scope.CapturedEntryCount,
            "captured scope entry count");
        RequireNonNegative(
            scope.CapturedRequestCount,
            "captured scope request count");
        RequireNonNegative(
            scope.CapturedResponseBytes,
            "captured scope response byte count");
        RequireSha(scope.ContentSha256, "scope content hash");
        if (scope.Status is
            CaptureScopeStatus.Incomplete or
            CaptureScopeStatus.Failed)
        {
            Invalid(
                CapturePackageFailureKind.IncompleteCapture,
                $"Capture scope {scope.Ordinal} is not complete.");
        }

        if (scope.Status == CaptureScopeStatus.Unsupported)
        {
            if (scope.DeclaredPageCount != 0 ||
                scope.DeclaredEntryCount != 0 ||
                scope.ProviderReportedTotalPages != 0 ||
                scope.ProviderReportedTotalEntries != 0 ||
                scope.CapturedPageCount != 0 ||
                scope.CapturedEntryCount != 0 ||
                scope.CapturedRequestCount != 0 ||
                scope.CapturedResponseBytes != 0)
            {
                Invalid(
                    CapturePackageFailureKind.AggregateMismatch,
                    $"Unsupported capture scope {scope.Ordinal} must have zero captured work.");
            }
            return;
        }

        if (scope.DeclaredPageCount !=
                scope.ProviderReportedTotalPages ||
            scope.DeclaredEntryCount !=
                scope.ProviderReportedTotalEntries)
        {
            Invalid(
                CapturePackageFailureKind.AggregateMismatch,
                $"Completed capture scope {scope.Ordinal} does not preserve provider totals.");
        }
        ValidateZeroResponseSemantics(
            pageIndex: 0,
            scope.ProviderReportedTotalPages,
            scope.ProviderReportedTotalEntries,
            entryCount: 0,
            $"scope {scope.Ordinal}");
        var expectedCapturedPages =
            scope.ProviderReportedTotalPages == 0
                ? 1
                : scope.ProviderReportedTotalPages;
        if (scope.CapturedPageCount != expectedCapturedPages ||
            scope.CapturedEntryCount !=
                scope.ProviderReportedTotalEntries ||
            scope.CapturedRequestCount < expectedCapturedPages ||
            scope.CapturedResponseBytes <= 0)
        {
            Invalid(
                CapturePackageFailureKind.AggregateMismatch,
                $"Completed capture scope {scope.Ordinal} does not satisfy captured response totals.");
        }
    }

    private static void ValidateRequest(
        CaptureRequestDescriptor request,
        IReadOnlyList<string> enabledSolo,
        IReadOnlyList<string> enabledBands)
    {
        RequireSafeText(request.SongId, "request song ID");
        ValidateScopeType(
            request.ScopeKind,
            request.LeaderboardType,
            enabledSolo,
            enabledBands);
        ValidateKnownStatus(request.Status);
        RequireNonNegative(request.ScopeOrdinal, "request scope ordinal");
        RequireNonNegative(
            request.ScopeRequestOrdinal,
            "scope request ordinal");
        RequireNonNegative(request.PageIndex, "request page index");
        if (request.ScopeRequestOrdinal != request.PageIndex)
        {
            Invalid(
                CapturePackageFailureKind.NonCanonicalOrder,
                $"Capture request {request.Ordinal} scope ordinal and page index differ.");
        }
        if (request.PageSize <= 0 ||
            request.PageSize > CapturePackageFormat.MaximumPageSize)
        {
            Invalid(
                CapturePackageFailureKind.RecordLimitExceeded,
                "Capture request page size is outside the supported bounds.");
        }
        RequireNonNegative(
            request.ProviderReportedTotalPages,
            "provider-reported request page count");
        RequireNonNegative(
            request.ProviderReportedTotalEntries,
            "provider-reported request entry count");
        if (request.ProviderReportedTotalPages >
            CapturePackageFormat.MaximumRequestRecords)
        {
            Invalid(
                CapturePackageFailureKind.RecordLimitExceeded,
                "Provider-reported request page count exceeds the capture limit.");
        }
        RequireNonNegative(
            request.CapturedPageCount,
            "captured request page count");
        RequireNonNegative(
            request.CapturedEntryCount,
            "captured request entry count");
        RequireNonNegative(
            request.CapturedRequestCount,
            "captured request count");
        RequireNonNegative(
            request.CapturedResponseBytes,
            "captured request response byte count");
        RequireNonNegative(
            request.ResponseOffset,
            "response member offset");
        if (request.ResponseLength <= 0 ||
            request.ResponseLength >
            CapturePackageFormat.MaximumResponseRecordBytes ||
            request.CapturedResponseBytes !=
                request.ResponseLength)
        {
            Invalid(
                CapturePackageFailureKind.RecordLimitExceeded,
                $"Capture request {request.Ordinal} response member length is invalid.");
        }
        RequireSha(request.ContentSha256, "request content hash");
        if (!CapturePackageFormat.TryParseResponseShardPath(
                request.ResponseShardPath,
                out _))
        {
            Invalid(
                CapturePackageFailureKind.InvalidMetadata,
                $"Capture request {request.Ordinal} response shard path is not canonical.");
        }
        if (request.Status != CaptureRequestStatus.Complete)
        {
            Invalid(
                CapturePackageFailureKind.IncompleteCapture,
                $"Capture request {request.Ordinal} is not complete.");
        }
        if (request.CapturedPageCount != 1 ||
            request.CapturedRequestCount <= 0 ||
            request.CapturedEntryCount >
                CapturePackageFormat.MaximumResponseEntries ||
            request.CapturedEntryCount > request.PageSize)
        {
            Invalid(
                CapturePackageFailureKind.AggregateMismatch,
                $"Capture request {request.Ordinal} does not represent one complete bounded response.");
        }
        ValidateZeroResponseSemantics(
            request.PageIndex,
            request.ProviderReportedTotalPages,
            request.ProviderReportedTotalEntries,
            request.CapturedEntryCount,
            $"request {request.Ordinal}");
    }

    private static void ValidateScopeUniverse(
        IReadOnlyList<CaptureScopeDescriptor> scopes,
        CaptureCatalogArtifact catalog,
        IReadOnlyList<string> enabledSolo,
        IReadOnlyList<string> enabledBands)
    {
        int expectedScopeCount;
        try
        {
            expectedScopeCount = checked(
                catalog.SongCount *
                (enabledSolo.Count + enabledBands.Count));
        }
        catch (OverflowException exception)
        {
            throw new CapturePackageException(
                CapturePackageFailureKind.RecordLimitExceeded,
                "Capture scope universe count overflowed.",
                exception);
        }
        if (expectedScopeCount <= 0 ||
            expectedScopeCount >
                CapturePackageFormat.MaximumScopeRecords ||
            scopes.Count != expectedScopeCount)
        {
            Invalid(
                CapturePackageFailureKind.MissingScope,
                "Capture scopes do not cover the exact catalog and enabled-type universe.");
        }

        var scopeIndex = 0;
        var completeScopeCount = 0;
        foreach (var song in catalog.Songs)
        {
            foreach (var leaderboardType in enabledSolo)
            {
                ValidateScopeAgainstCatalog(
                    scopes[scopeIndex++],
                    song,
                    CaptureScopeKind.Solo,
                    leaderboardType,
                    ref completeScopeCount);
            }
            foreach (var leaderboardType in enabledBands)
            {
                ValidateScopeAgainstCatalog(
                    scopes[scopeIndex++],
                    song,
                    CaptureScopeKind.Band,
                    leaderboardType,
                    ref completeScopeCount);
            }
        }
        if (completeScopeCount == 0)
        {
            Invalid(
                CapturePackageFailureKind.IncompleteCapture,
                "v1 does not seal zero-request or all-unsupported capture packages.");
        }
    }

    private static void ValidateScopeAgainstCatalog(
        CaptureScopeDescriptor scope,
        CaptureCatalogSong song,
        CaptureScopeKind scopeKind,
        string leaderboardType,
        ref int completeScopeCount)
    {
        if (!string.Equals(
                scope.SongId,
                song.SongId,
                StringComparison.Ordinal) ||
            scope.ScopeKind != scopeKind ||
            !string.Equals(
                scope.LeaderboardType,
                leaderboardType,
                StringComparison.Ordinal))
        {
            Invalid(
                CapturePackageFailureKind.NonCanonicalOrder,
                $"Capture scope {scope.Ordinal} does not match the canonical catalog scope sequence.");
        }

        var support = GetCatalogSupport(
            song,
            scopeKind,
            leaderboardType);
        var expectedStatus =
            support == CaptureCatalogSupportStatus.Supported
                ? CaptureScopeStatus.Complete
                : CaptureScopeStatus.Unsupported;
        if (scope.Status != expectedStatus)
        {
            Invalid(
                CapturePackageFailureKind.IncompleteCapture,
                $"Capture scope {scope.Ordinal} status is not proven by catalog support metadata.");
        }
        if (scope.Status == CaptureScopeStatus.Complete)
            completeScopeCount++;
    }

    private static CaptureCatalogSupportStatus GetCatalogSupport(
        CaptureCatalogSong song,
        CaptureScopeKind scopeKind,
        string leaderboardType)
    {
        foreach (var support in song.ScopeSupport)
        {
            if (support.ScopeKind == scopeKind &&
                string.Equals(
                    support.LeaderboardType,
                    leaderboardType,
                    StringComparison.Ordinal))
            {
                return support.Status;
            }
        }
        Invalid(
            CapturePackageFailureKind.MissingScope,
            $"Capture catalog song '{song.SongId}' lacks explicit support evidence for '{leaderboardType}'.");
        return default;
    }

    private static void ValidateRequestsAgainstScopes(
        IReadOnlyList<CaptureRequestDescriptor> requests,
        IReadOnlyList<CaptureScopeDescriptor> scopes)
    {
        var requestIndex = 0;
        foreach (var scope in scopes)
        {
            var scopeRequestCount = 0;
            var capturedPages = 0;
            long capturedEntries = 0;
            long capturedRequests = 0;
            long capturedBytes = 0;
            using var hash = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);

            while (requestIndex < requests.Count &&
                   requests[requestIndex].ScopeOrdinal ==
                   scope.Ordinal)
            {
                var request = requests[requestIndex];
                if (request.ScopeRequestOrdinal !=
                        scopeRequestCount ||
                    request.PageIndex != scopeRequestCount)
                {
                    Invalid(
                        CapturePackageFailureKind.MissingOrdinal,
                        $"Capture scope {scope.Ordinal} request/page ordinals are not contiguous.");
                }
                if (!string.Equals(
                        request.SongId,
                        scope.SongId,
                        StringComparison.Ordinal) ||
                    request.ScopeKind != scope.ScopeKind ||
                    !string.Equals(
                        request.LeaderboardType,
                        scope.LeaderboardType,
                        StringComparison.Ordinal) ||
                    request.ProviderReportedTotalPages !=
                        scope.ProviderReportedTotalPages ||
                    request.ProviderReportedTotalEntries !=
                        scope.ProviderReportedTotalEntries)
                {
                    Invalid(
                        CapturePackageFailureKind.AggregateMismatch,
                        $"Capture request {request.Ordinal} does not match scope {scope.Ordinal}.");
                }

                capturedPages = CheckedAdd(
                    capturedPages,
                    request.CapturedPageCount);
                capturedEntries = CheckedAdd(
                    capturedEntries,
                    request.CapturedEntryCount);
                capturedRequests = CheckedAdd(
                    capturedRequests,
                    request.CapturedRequestCount);
                capturedBytes = CheckedAdd(
                    capturedBytes,
                    request.CapturedResponseBytes);
                AppendScopeRequestFingerprint(hash, request);
                scopeRequestCount++;
                requestIndex++;
            }

            var expectedRequestCount =
                scope.Status == CaptureScopeStatus.Unsupported
                    ? 0
                    : scope.ProviderReportedTotalPages == 0
                        ? 1
                        : scope.ProviderReportedTotalPages;
            if (scopeRequestCount != expectedRequestCount)
            {
                Invalid(
                    CapturePackageFailureKind.MissingOrdinal,
                    $"Capture scope {scope.Ordinal} request count does not match its provider page semantics.");
            }
            if (scope.CapturedPageCount != capturedPages ||
                scope.CapturedEntryCount != capturedEntries ||
                scope.CapturedRequestCount != capturedRequests ||
                scope.CapturedResponseBytes != capturedBytes)
            {
                Invalid(
                    CapturePackageFailureKind.AggregateMismatch,
                    $"Capture scope {scope.Ordinal} aggregate counts do not match its requests.");
            }

            var expectedHash =
                Convert.ToHexString(hash.GetHashAndReset())
                    .ToLowerInvariant();
            if (!string.Equals(
                    scope.ContentSha256,
                    expectedHash,
                    StringComparison.Ordinal))
            {
                Invalid(
                    CapturePackageFailureKind.InvalidHash,
                    $"Capture scope {scope.Ordinal} content hash does not match its request descriptors.");
            }
        }

        if (requestIndex != requests.Count)
        {
            Invalid(
                CapturePackageFailureKind.MissingScope,
                $"Capture request {requests[requestIndex].Ordinal} references a missing or noncanonical scope.");
        }
    }

    private static void ValidateAggregates(
        CapturePackageDefinition definition,
        IReadOnlyList<CaptureRequestDescriptor> requests,
        IReadOnlyList<CaptureScopeDescriptor> scopes)
    {
        if (definition.TotalScopeCount != scopes.Count ||
            definition.TotalPageCount != requests.Count ||
            definition.TotalPageCount != CheckedSumInt(
                requests,
                static request => request.CapturedPageCount) ||
            definition.TotalEntryCount != CheckedSumLong(
                requests,
                static request => request.CapturedEntryCount) ||
            definition.TotalRequestCount != CheckedSumLong(
                requests,
                static request => request.CapturedRequestCount) ||
            definition.TotalResponseBytes != CheckedSumLong(
                requests,
                static request => request.CapturedResponseBytes) ||
            definition.TotalPageCount != CheckedSumInt(
                scopes,
                static scope => scope.CapturedPageCount) ||
            definition.TotalEntryCount != CheckedSumLong(
                scopes,
                static scope => scope.CapturedEntryCount) ||
            definition.TotalRequestCount != CheckedSumLong(
                scopes,
                static scope => scope.CapturedRequestCount) ||
            definition.TotalResponseBytes != CheckedSumLong(
                scopes,
                static scope => scope.CapturedResponseBytes))
        {
            Invalid(
                CapturePackageFailureKind.AggregateMismatch,
                "Capture package aggregate counts do not match request and scope descriptors.");
        }
    }

    private static CaptureDescriptorSetReference ValidateReference(
        CaptureDescriptorSetReference reference,
        string expectedPath,
        int expectedCount,
        int maximumRecords,
        long maximumBytes,
        string description)
    {
        if (!string.Equals(
                reference.Path,
                expectedPath,
                StringComparison.Ordinal) ||
            reference.SchemaVersion !=
                CapturePackageFormat.DescriptorSchemaVersion ||
            reference.RecordCount != expectedCount)
        {
            Invalid(
                CapturePackageFailureKind.InvalidMetadata,
                $"Capture {description} reference is invalid.");
        }
        ValidateRecordCount(
            reference.RecordCount,
            maximumRecords,
            description);
        if (reference.Bytes <= 0 ||
            reference.Bytes > maximumBytes ||
            reference.RecordCount >
            reference.Bytes /
            CapturePackageFormat.MinimumCanonicalJsonLineBytes)
        {
            Invalid(
                CapturePackageFailureKind.RecordLimitExceeded,
                $"Capture {description} count and byte bounds are invalid.");
        }
        RequireSha(reference.Sha256, $"{description} hash");
        return reference;
    }

    private static TierZeroPackageDraft NormalizeEnvelope(
        TierZeroPackageDraft envelope)
    {
        try
        {
            return TierZeroPackageModel.NormalizeDraft(envelope);
        }
        catch (TierZeroPackageException exception)
        {
            throw new CapturePackageException(
                CapturePackageFailureKind.InvalidMetadata,
                "Tier-0 capture envelope identity is invalid.",
                exception);
        }
    }

    private static void ValidateEnvelopeStrings(
        TierZeroPackageDraft envelope)
    {
        RequireSafeText(envelope.PackageId, "envelope package ID");
        RequireSafeText(
            envelope.ProducerIdentity,
            "envelope producer identity");
        RequireSafeText(
            envelope.Source.Catalog.Identity,
            "source catalog identity");
        RequireSafeText(
            envelope.Build.ServiceVersion,
            "service version");
        foreach (var extension in envelope.Database.Extensions)
            RequireSafeText(extension, "database extension");
        foreach (var key in envelope.Configuration.Keys)
            RequireSafeText(key, "configuration key");
        foreach (var parent in envelope.ParentRootHashes)
        {
            RequireSafeText(
                parent.LogicalParent,
                "parent logical identity");
        }
    }

    private static void ValidateScopeType(
        CaptureScopeKind kind,
        string leaderboardType,
        IReadOnlyList<string> enabledSolo,
        IReadOnlyList<string> enabledBands)
    {
        ValidateKnownScopeKind(kind);
        RequireSafeText(leaderboardType, "leaderboard type");
        var valid = kind switch
        {
            CaptureScopeKind.Solo =>
                enabledSolo.Contains(
                    leaderboardType,
                    StringComparer.Ordinal),
            CaptureScopeKind.Band =>
                enabledBands.Contains(
                    leaderboardType,
                    StringComparer.Ordinal),
            _ => false,
        };
        if (!valid)
        {
            Invalid(
                CapturePackageFailureKind.MissingScope,
                $"Leaderboard type '{leaderboardType}' is not enabled for scope kind '{kind}'.");
        }
    }

    private static void ValidateKnownLeaderboardType(
        CaptureScopeKind kind,
        string leaderboardType)
    {
        RequireSafeText(leaderboardType, "leaderboard type");
        var known = kind switch
        {
            CaptureScopeKind.Solo =>
                CapturePackageFormat.SoloInstrumentOrder.Contains(
                    leaderboardType,
                    StringComparer.Ordinal),
            CaptureScopeKind.Band =>
                CapturePackageFormat.BandTypeOrder.Contains(
                    leaderboardType,
                    StringComparer.Ordinal),
            _ => false,
        };
        if (!known)
        {
            Invalid(
                CapturePackageFailureKind.InvalidMetadata,
                $"Capture leaderboard type '{leaderboardType}' is unsupported.");
        }
    }

    private static void ValidateKnownStatus(CapturePackageStatus status)
    {
        if (!Enum.IsDefined(status))
        {
            Invalid(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture package status is unsupported.");
        }
    }

    private static void ValidateKnownStatus(CaptureScopeStatus status)
    {
        if (!Enum.IsDefined(status))
        {
            Invalid(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture scope status is unsupported.");
        }
    }

    private static void ValidateKnownStatus(CaptureRequestStatus status)
    {
        if (!Enum.IsDefined(status))
        {
            Invalid(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture request status is unsupported.");
        }
    }

    private static void ValidateKnownScopeKind(CaptureScopeKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            Invalid(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture scope kind is unsupported.");
        }
    }

    private static void ValidateKnownResponseKind(
        CaptureResponseKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            Invalid(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture response kind is unsupported.");
        }
    }

    private static CaptureResponseKind ExpectedResponseKind(
        CaptureScopeKind kind) =>
        kind switch
        {
            CaptureScopeKind.Solo =>
                CaptureResponseKind.SoloLeaderboardPage,
            CaptureScopeKind.Band =>
                CaptureResponseKind.BandLeaderboardPage,
            _ => throw new CapturePackageException(
                CapturePackageFailureKind.InvalidMetadata,
                "Capture scope kind is unsupported."),
        };

    private static void ValidateZeroResponseSemantics(
        int pageIndex,
        int providerReportedTotalPages,
        long providerReportedTotalEntries,
        long entryCount,
        string description)
    {
        var zeroUniverse =
            providerReportedTotalPages == 0 ||
            providerReportedTotalEntries == 0;
        if (zeroUniverse)
        {
            if (providerReportedTotalPages != 0 ||
                providerReportedTotalEntries != 0 ||
                pageIndex != 0 ||
                entryCount != 0)
            {
                Invalid(
                    CapturePackageFailureKind.AggregateMismatch,
                    $"Capture {description} has inconsistent zero-page/zero-entry semantics.");
            }
            return;
        }

        if (pageIndex >= providerReportedTotalPages ||
            entryCount > providerReportedTotalEntries)
        {
            Invalid(
                CapturePackageFailureKind.AggregateMismatch,
                $"Capture {description} page coordinates or entry count exceed provider totals.");
        }
    }

    private static void ValidateOrdinals<T>(
        IReadOnlyList<T> values,
        Func<T, int> ordinal,
        string description)
    {
        var seen = new bool[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            var value = ordinal(values[index]);
            if (value < 0 ||
                value >= values.Count)
            {
                Invalid(
                    CapturePackageFailureKind.MissingOrdinal,
                    $"Capture {description} ordinals must be contiguous from zero.");
            }
            if (seen[value])
            {
                Invalid(
                    CapturePackageFailureKind.DuplicateOrdinal,
                    $"Capture {description} ordinals must be unique.");
            }
            seen[value] = true;
            if (value != index)
            {
                Invalid(
                    CapturePackageFailureKind.NonCanonicalOrder,
                    $"Capture {description} descriptors are not in ordinal order.");
            }
        }
    }

    private static int CheckedSumInt<T>(
        IEnumerable<T> values,
        Func<T, int> selector)
    {
        try
        {
            return values.Aggregate(
                0,
                (total, value) => checked(total + selector(value)));
        }
        catch (OverflowException exception)
        {
            throw new CapturePackageException(
                CapturePackageFailureKind.AggregateMismatch,
                "Capture count aggregate overflowed.",
                exception);
        }
    }

    private static long CheckedSumLong<T>(
        IEnumerable<T> values,
        Func<T, long> selector)
    {
        try
        {
            return values.Aggregate(
                0L,
                (total, value) => checked(total + selector(value)));
        }
        catch (OverflowException exception)
        {
            throw new CapturePackageException(
                CapturePackageFailureKind.AggregateMismatch,
                "Capture count aggregate overflowed.",
                exception);
        }
    }

    private static int CheckedAdd(int first, int second)
    {
        try
        {
            return checked(first + second);
        }
        catch (OverflowException exception)
        {
            throw new CapturePackageException(
                CapturePackageFailureKind.AggregateMismatch,
                "Capture count aggregate overflowed.",
                exception);
        }
    }

    private static long CheckedAdd(long first, long second)
    {
        try
        {
            return checked(first + second);
        }
        catch (OverflowException exception)
        {
            throw new CapturePackageException(
                CapturePackageFailureKind.AggregateMismatch,
                "Capture count aggregate overflowed.",
                exception);
        }
    }

    private static void AppendScopeRequestFingerprint(
        IncrementalHash hash,
        CaptureRequestDescriptor request)
    {
        Span<byte> bytes = stackalloc byte[88];
        var offset = 0;
        WriteInt32(bytes, ref offset, request.Ordinal);
        WriteInt32(bytes, ref offset, request.ScopeOrdinal);
        WriteInt32(
            bytes,
            ref offset,
            request.ScopeRequestOrdinal);
        WriteInt32(bytes, ref offset, (int)request.ScopeKind);
        WriteInt32(bytes, ref offset, LeaderboardTypeOrdinal(
            request.ScopeKind,
            request.LeaderboardType));
        WriteInt32(bytes, ref offset, request.PageIndex);
        WriteInt32(bytes, ref offset, request.PageSize);
        WriteInt32(
            bytes,
            ref offset,
            request.ProviderReportedTotalPages);
        WriteInt64(
            bytes,
            ref offset,
            request.ProviderReportedTotalEntries);
        WriteInt32(
            bytes,
            ref offset,
            request.CapturedPageCount);
        WriteInt64(
            bytes,
            ref offset,
            request.CapturedEntryCount);
        WriteInt64(
            bytes,
            ref offset,
            request.CapturedRequestCount);
        WriteInt64(
            bytes,
            ref offset,
            request.CapturedResponseBytes);
        WriteInt32(bytes, ref offset, (int)request.Status);
        if (!CapturePackageFormat.TryParseResponseShardPath(
                request.ResponseShardPath,
                out var shardOrdinal))
        {
            Invalid(
                CapturePackageFailureKind.InvalidMetadata,
                $"Capture request {request.Ordinal} response shard path is not canonical.");
        }
        WriteInt32(bytes, ref offset, shardOrdinal);
        WriteInt64(bytes, ref offset, request.ResponseOffset);
        WriteInt32(bytes, ref offset, request.ResponseLength);
        hash.AppendData(bytes[..offset]);

        Span<byte> contentHash = stackalloc byte[32];
        if (!TryDecodeSha256(
                request.ContentSha256,
                contentHash))
        {
            Invalid(
                CapturePackageFailureKind.InvalidHash,
                "Capture request content hash is invalid.");
        }
        hash.AppendData(contentHash);
    }

    private static void WriteInt32(
        Span<byte> destination,
        ref int offset,
        int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(
            destination[offset..],
            value);
        offset += sizeof(int);
    }

    private static void WriteInt64(
        Span<byte> destination,
        ref int offset,
        long value)
    {
        BinaryPrimitives.WriteInt64BigEndian(
            destination[offset..],
            value);
        offset += sizeof(long);
    }

    private static bool TryDecodeSha256(
        string value,
        Span<byte> destination)
    {
        if (!TierZeroCanonicalJson.IsSha256(value) ||
            destination.Length < 32)
        {
            return false;
        }
        for (var index = 0; index < 32; index++)
        {
            destination[index] = (byte)(
                HexValue(value[index * 2]) * 16 +
                HexValue(value[index * 2 + 1]));
        }
        return true;
    }

    private static int HexValue(char value) =>
        value is >= '0' and <= '9'
            ? value - '0'
            : value - 'a' + 10;

    private static int LeaderboardTypeOrdinal(
        CaptureScopeKind scopeKind,
        string leaderboardType)
    {
        var values = scopeKind == CaptureScopeKind.Solo
            ? CapturePackageFormat.SoloInstrumentOrder
            : CapturePackageFormat.BandTypeOrder;
        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(
                    values[index],
                    leaderboardType,
                    StringComparison.Ordinal))
            {
                return index;
            }
        }
        Invalid(
            CapturePackageFailureKind.InvalidMetadata,
            $"Capture leaderboard type '{leaderboardType}' is unsupported.");
        return default;
    }

    private static string ComputeManifestRootHash(
        CapturePackageManifest manifest) =>
        TierZeroCanonicalJson.Sha256Hex(
            TierZeroCanonicalJson.Serialize(
                manifest with { ManifestRootHash = null }));

    private static void RequireSafeText(
        string? value,
        string description)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(
                value,
                value.Trim(),
                StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            Invalid(
                CapturePackageFailureKind.InvalidMetadata,
                $"Capture {description} must be a canonical non-empty printable string.");
        }
        if (TierZeroConfigurationFingerprinter.IsSecretLikeValue(value))
        {
            Invalid(
                CapturePackageFailureKind.InvalidMetadata,
                $"Capture {description} cannot contain credentials, endpoints, or authorization material.");
        }
    }

    private static void RequireSha(
        string? value,
        string description)
    {
        if (!TierZeroCanonicalJson.IsSha256(value))
        {
            Invalid(
                CapturePackageFailureKind.InvalidHash,
                $"Capture {description} must be a lowercase SHA-256 value.");
        }
    }

    private static void RequireNonNegative(
        long value,
        string description)
    {
        if (value < 0)
        {
            Invalid(
                CapturePackageFailureKind.NegativeCount,
                $"Capture {description} cannot be negative.");
        }
    }

    private static void ValidateRecordCount(
        int count,
        int maximum,
        string description)
    {
        if (count <= 0 ||
            count > maximum)
        {
            Invalid(
                CapturePackageFailureKind.RecordLimitExceeded,
                $"Capture {description} count is outside the supported bounds.");
        }
    }

    private static void ValidateRegistration(
        TierZeroArtifactRegistration registration,
        string owner,
        string mediaType,
        int schemaVersion,
        long rowCount,
        long bytes,
        string description)
    {
        if (!string.Equals(
                registration.LogicalOwner,
                owner,
                StringComparison.Ordinal) ||
            !string.Equals(
                registration.MediaType,
                mediaType,
                StringComparison.Ordinal) ||
            registration.SchemaVersion != schemaVersion ||
            registration.RowCount != rowCount ||
            registration.UncompressedBytes != bytes ||
            registration.Ranges is { Count: > 0 })
        {
            Invalid(
                CapturePackageFailureKind.ArtifactMismatch,
                $"Capture {description} registration does not match its canonical contract.");
        }
    }

    private static IReadOnlyDictionary<string, TierZeroArtifactDescriptor>
        ToArtifactDictionary(
            IReadOnlyList<TierZeroArtifactDescriptor> artifacts)
    {
        var byPath = new Dictionary<string, TierZeroArtifactDescriptor>(
            artifacts.Count,
            StringComparer.Ordinal);
        foreach (var artifact in artifacts)
        {
            if (!byPath.TryAdd(artifact.Path, artifact))
            {
                Invalid(
                    CapturePackageFailureKind.ArtifactMismatch,
                    $"Capture artifact path '{artifact.Path}' is duplicated.");
            }
        }
        return byPath;
    }

    private static async Task ValidateDescriptorArtifactAgainstRowsAsync<T>(
        string rootPath,
        TierZeroArtifactDescriptor artifact,
        CaptureJsonLinesMeasurement measurement,
        int maximumRecords,
        int maximumRecordBytes,
        string description,
        IReadOnlyList<T> expectedRows,
        CancellationToken cancellationToken)
    {
        var read = await ReadJsonLinesArtifactAsync<T>(
            rootPath,
            artifact,
            measurement.RecordCount,
            measurement.Bytes,
            maximumRecords,
            CapturePackageFormat.MaximumDescriptorSetBytes,
            maximumRecordBytes,
            description,
            preDeserialize: null,
            (row, index, _, _, _) =>
            {
                if (!CanonicalEquals(row, expectedRows[index]))
                {
                    Invalid(
                        CapturePackageFailureKind.ArtifactMismatch,
                        $"{description} record {index} does not match the sealed definition.");
                }
            },
            cancellationToken);
        if (!string.Equals(
                read.Sha256,
                measurement.Sha256,
                StringComparison.Ordinal))
        {
            Invalid(
                CapturePackageFailureKind.ArtifactMismatch,
                $"{description} hash does not match the sealed definition.");
        }
    }

    internal static async Task ValidateResponseShardsAsync(
        string rootPath,
        CapturePackageDefinition definition,
        IReadOnlyDictionary<string, TierZeroArtifactDescriptor> byPath,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, TierZeroPackageFile>?
            expectedFiles = null)
    {
        foreach (var layout in BuildResponseShardLayouts(
                     definition.Requests))
        {
            var artifact = RequireArtifact(byPath, layout.Path);
            await ReadJsonLinesArtifactAsync<CaptureResponseArtifact>(
                rootPath,
                artifact,
                layout.RecordCount,
                layout.Bytes,
                CapturePackageFormat.MaximumRequestRecords,
                CapturePackageFormat.MaximumResponseShardBytes,
                CapturePackageFormat.MaximumResponseRecordBytes,
                $"Capture response shard '{layout.Path}'",
                PreflightResponseRecord,
                (response, memberIndex, offset, length, sha256) =>
                {
                    var request = definition.Requests[
                        layout.StartRequestIndex + memberIndex];
                    ValidateResponseAgainstRequest(
                        ValidateResponse(response),
                        request,
                        offset,
                        length,
                        sha256);
                },
                cancellationToken,
                ExpectedSnapshot(
                    expectedFiles,
                    layout.Path));
        }
    }

    private static TierZeroFileSnapshot? ExpectedSnapshot(
        IReadOnlyDictionary<string, TierZeroPackageFile>?
            expectedFiles,
        string path)
    {
        if (expectedFiles is null)
            return null;
        if (expectedFiles.TryGetValue(path, out var expectedFile))
            return expectedFile.Snapshot;
        Invalid(
            CapturePackageFailureKind
                .TierZeroVerificationFailed,
            $"Capture package inventory is missing '{path}'.");
        return null;
    }

    private static void ValidateResponseAgainstRequest(
        CaptureResponseArtifact response,
        CaptureRequestDescriptor request,
        long offset,
        int length,
        string sha256)
    {
        if (response.RequestOrdinal != request.Ordinal ||
            response.ScopeOrdinal != request.ScopeOrdinal ||
            response.ScopeRequestOrdinal !=
                request.ScopeRequestOrdinal ||
            response.ResponseKind !=
                ExpectedResponseKind(request.ScopeKind) ||
            !string.Equals(
                response.SongId,
                request.SongId,
                StringComparison.Ordinal) ||
            response.ScopeKind != request.ScopeKind ||
            !string.Equals(
                response.LeaderboardType,
                request.LeaderboardType,
                StringComparison.Ordinal) ||
            response.PageIndex != request.PageIndex ||
            response.PageSize != request.PageSize ||
            response.ProviderReportedTotalPages !=
                request.ProviderReportedTotalPages ||
            response.ProviderReportedTotalEntries !=
                request.ProviderReportedTotalEntries ||
            response.EntryCount != request.CapturedEntryCount ||
            offset != request.ResponseOffset ||
            length != request.ResponseLength ||
            length != request.CapturedResponseBytes ||
            !string.Equals(
                sha256,
                request.ContentSha256,
                StringComparison.Ordinal))
        {
            Invalid(
                CapturePackageFailureKind.ArtifactMismatch,
                $"Capture response member for request {request.Ordinal} does not match its descriptor.");
        }
    }

    private static void ValidateReadableArtifact(
        TierZeroArtifactDescriptor artifact,
        long maximumBytes)
    {
        if (artifact.CompressedBytes <= 0 ||
            artifact.CompressedBytes > maximumBytes ||
            artifact.CompressedBytes > int.MaxValue ||
            artifact.CompressedBytes != artifact.UncompressedBytes)
        {
            Invalid(
                CapturePackageFailureKind.ArtifactMismatch,
                $"Capture artifact '{artifact.Path}' exceeds its reader bound.");
        }
    }

    private static string ResolveArtifactPath(
        string rootPath,
        string artifactPath)
    {
        if (!string.Equals(
                artifactPath,
                TierZeroPackagePath.Normalize(artifactPath),
                StringComparison.Ordinal))
        {
            Invalid(
                CapturePackageFailureKind.InvalidMetadata,
                $"Capture artifact path '{artifactPath}' is not canonical.");
        }
        var path = TierZeroPackagePath.ResolveUnderRoot(
            rootPath,
            artifactPath);
        TierZeroPackagePath.EnsureNoSymbolicLinks(
            rootPath,
            path,
            includeCandidate: true);
        return path;
    }

    private static void PreflightJsonStructure(
        ReadOnlySpan<byte> bytes,
        string requiredArrayProperty,
        int maximumCount,
        string description,
        IReadOnlyDictionary<string, int>? nestedArrayLimits = null)
    {
        try
        {
            var reader = new Utf8JsonReader(
                bytes,
                new JsonReaderOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64,
                });
            if (!reader.Read() ||
                reader.TokenType != JsonTokenType.StartObject)
            {
                Invalid(
                    CapturePackageFailureKind.InvalidMetadata,
                    $"{description} must be contained in a JSON object.");
            }

            var limits = new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase)
            {
                [requiredArrayProperty] = maximumCount,
            };
            if (nestedArrayLimits is not null)
            {
                foreach (var (name, limit) in nestedArrayLimits)
                    limits.Add(name, limit);
            }

            var requiredArraySeen = false;
            PreflightObject(
                ref reader,
                isRoot: true,
                applyNestedArrayLimits: false,
                requiredArrayProperty,
                limits,
                nestedArrayLimits,
                description,
                ref requiredArraySeen);
            if (!requiredArraySeen)
            {
                Invalid(
                    CapturePackageFailureKind.InvalidMetadata,
                    $"{description} are missing.");
            }
            if (reader.Read())
            {
                Invalid(
                    CapturePackageFailureKind.NonCanonicalJson,
                    $"{description} contain trailing JSON content.");
            }
        }
        catch (JsonException exception)
        {
            throw new CapturePackageException(
                CapturePackageFailureKind.InvalidMetadata,
                $"{description} are invalid JSON.",
                exception);
        }
    }

    private static void PreflightObject(
        ref Utf8JsonReader reader,
        bool isRoot,
        bool applyNestedArrayLimits,
        string requiredArrayProperty,
        IReadOnlyDictionary<string, int> arrayLimits,
        IReadOnlyDictionary<string, int>? nestedArrayLimits,
        string description,
        ref bool requiredArraySeen)
    {
        var properties = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return;
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                Invalid(
                    CapturePackageFailureKind.InvalidMetadata,
                    $"{description} contain an invalid JSON object.");
            }

            var propertyName =
                reader.GetString() ??
                throw new CapturePackageException(
                    CapturePackageFailureKind.InvalidMetadata,
                    $"{description} contain a null JSON property name.");
            if (!properties.Add(propertyName))
            {
                Invalid(
                    CapturePackageFailureKind.NonCanonicalJson,
                    $"{description} contain duplicate or case-aliased JSON property '{propertyName}'.");
            }

            string? canonicalArrayProperty = null;
            if (isRoot &&
                string.Equals(
                    requiredArrayProperty,
                    propertyName,
                    StringComparison.OrdinalIgnoreCase))
            {
                canonicalArrayProperty = requiredArrayProperty;
            }
            else if (applyNestedArrayLimits &&
                     nestedArrayLimits is not null)
            {
                canonicalArrayProperty =
                    nestedArrayLimits.Keys.FirstOrDefault(candidate =>
                        string.Equals(
                            candidate,
                            propertyName,
                            StringComparison.OrdinalIgnoreCase));
            }
            if (canonicalArrayProperty is not null &&
                !string.Equals(
                    canonicalArrayProperty,
                    propertyName,
                    StringComparison.Ordinal))
            {
                Invalid(
                    CapturePackageFailureKind.NonCanonicalJson,
                    $"{description} contain noncanonical JSON property '{propertyName}'.");
            }
            if (isRoot &&
                string.Equals(
                    propertyName,
                    requiredArrayProperty,
                    StringComparison.Ordinal))
            {
                requiredArraySeen = true;
            }

            if (!reader.Read())
            {
                Invalid(
                    CapturePackageFailureKind.InvalidMetadata,
                    $"{description} end before property '{propertyName}' has a value.");
            }
            if (canonicalArrayProperty is not null &&
                reader.TokenType != JsonTokenType.StartArray)
            {
                Invalid(
                    CapturePackageFailureKind.InvalidMetadata,
                    $"{description} property '{propertyName}' must be a JSON array.");
            }
            PreflightValue(
                ref reader,
                canonicalArrayProperty is null
                    ? null
                    : arrayLimits[canonicalArrayProperty],
                applyNestedArrayLimitsToArrayItems:
                    isRoot &&
                    canonicalArrayProperty is not null &&
                    nestedArrayLimits is not null,
                requiredArrayProperty,
                arrayLimits,
                nestedArrayLimits,
                description,
                ref requiredArraySeen);
        }

        Invalid(
            CapturePackageFailureKind.InvalidMetadata,
            $"{description} contain an unterminated JSON object.");
    }

    private static void PreflightValue(
        ref Utf8JsonReader reader,
        int? arrayLimit,
        bool applyNestedArrayLimitsToArrayItems,
        string requiredArrayProperty,
        IReadOnlyDictionary<string, int> arrayLimits,
        IReadOnlyDictionary<string, int>? nestedArrayLimits,
        string description,
        ref bool requiredArraySeen)
    {
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            PreflightObject(
                ref reader,
                isRoot: false,
                applyNestedArrayLimits:
                    applyNestedArrayLimitsToArrayItems,
                requiredArrayProperty,
                arrayLimits,
                nestedArrayLimits,
                description,
                ref requiredArraySeen);
            return;
        }
        if (reader.TokenType != JsonTokenType.StartArray)
            return;

        var limit = arrayLimit ?? int.MaxValue;
        var count = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                return;
            count++;
            if (count > limit)
            {
                Invalid(
                    CapturePackageFailureKind.RecordLimitExceeded,
                    $"{description} exceed a supported array record ceiling.");
            }
            PreflightValue(
                ref reader,
                arrayLimit: null,
                applyNestedArrayLimitsToArrayItems:
                    applyNestedArrayLimitsToArrayItems,
                requiredArrayProperty,
                arrayLimits,
                nestedArrayLimits,
                description,
                ref requiredArraySeen);
        }

        Invalid(
            CapturePackageFailureKind.InvalidMetadata,
            $"{description} contain an unterminated JSON array.");
    }

    private static void ValidateJsonElementProperties(
        JsonElement element,
        string description)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var properties = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!properties.Add(property.Name))
                {
                    Invalid(
                        CapturePackageFailureKind.NonCanonicalJson,
                        $"{description} contains duplicate or case-aliased JSON property '{property.Name}'.");
                }
                ValidateJsonElementProperties(
                    property.Value,
                    description);
            }
            return;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateJsonElementProperties(
                    item,
                    description);
            }
        }
    }

    [DoesNotReturn]
    private static void Invalid(
        CapturePackageFailureKind kind,
        string message) =>
        throw new CapturePackageException(kind, message);

    private readonly record struct CaptureScopeKey(
        CaptureScopeKind ScopeKind,
        string LeaderboardType);
}

internal sealed record CaptureResponseShardLayout(
    int Ordinal,
    string Path,
    int StartRequestIndex,
    int RecordCount,
    long Bytes);
