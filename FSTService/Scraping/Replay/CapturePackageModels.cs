using System.Globalization;
using System.Text.Json;

namespace FSTService.Scraping.Replay;

public static class CapturePackageFormat
{
    public const string FormatId = "fst.capture-package.v1";
    public const string CatalogFormatId = "fst.capture-catalog.v1";
    public const string ResponseFormatId = "fst.capture-response.v1";
    public const string ProviderId = "epic-games";
    public const int Version = 1;
    public const int CatalogSchemaVersion = 1;
    public const int DescriptorSchemaVersion = 1;
    public const int ResponseSchemaVersion = 1;
    public const string ManifestPath = "capture/manifest.json";
    public const string CatalogPath = "capture/catalog.json";
    public const string RequestPlanPath = "capture/request-plan.jsonl";
    public const string ScopeCompletenessPath =
        "capture/scope-completeness.jsonl";
    public const string ManifestOwner = "capture-package-manifest";
    public const string CatalogOwner = "capture-catalog";
    public const string RequestPlanOwner = "capture-request-plan";
    public const string ScopeCompletenessOwner =
        "capture-scope-completeness";
    public const string ResponseShardOwner = "capture-response-shard";
    public const string JsonMediaType = "application/json";
    public const string JsonLinesMediaType = "application/x-ndjson";
    public const long MaximumManifestBytes = 4L * 1024 * 1024;
    public const long MaximumCatalogBytes = 16L * 1024 * 1024;
    public const long MaximumDescriptorSetBytes = 512L * 1024 * 1024;
    public const long MaximumResponseShardBytes = 64L * 1024 * 1024;
    public const int MaximumCatalogSongs = 100_000;
    public const int MaximumScopeRecords = 100_000;
    public const int MaximumRequestRecords = 1_000_000;
    public const int MaximumResponseShards = 2_048;
    public const int MaximumPackageFileSystemEntries =
        MaximumResponseShards + 16;
    public const int MaximumPageSize = 10_000;
    public const int MaximumResponseEntries = 10_000;
    public const int MaximumRequestDescriptorBytes = 8 * 1024;
    public const int MaximumScopeDescriptorBytes = 8 * 1024;
    public const int MaximumResponseRecordBytes = 8 * 1024 * 1024;
    public const int MinimumCanonicalJsonLineBytes = 3;

    public static IReadOnlyList<string> SoloInstrumentOrder { get; } =
        Array.AsReadOnly(
        [
            "Solo_Guitar",
            "Solo_Bass",
            "Solo_Vocals",
            "Solo_Drums",
            "Solo_PeripheralGuitar",
            "Solo_PeripheralBass",
            "Solo_PeripheralVocals",
            "Solo_PeripheralCymbals",
            "Solo_PeripheralDrums",
        ]);

    public static IReadOnlyList<string> BandTypeOrder { get; } =
        Array.AsReadOnly(
        [
            "Band_Duets",
            "Band_Trios",
            "Band_Quad",
        ]);

    public static string ResponseShardPath(int shardOrdinal)
    {
        if (shardOrdinal < 0 ||
            shardOrdinal >= MaximumResponseShards)
        {
            throw new ArgumentOutOfRangeException(
                nameof(shardOrdinal),
                $"Capture response shard ordinals must be between 0 and {MaximumResponseShards - 1}.");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"capture/responses/{shardOrdinal:D4}.jsonl");
    }

    internal static bool TryParseResponseShardPath(
        string? path,
        out int shardOrdinal)
    {
        shardOrdinal = -1;
        if (string.IsNullOrEmpty(path))
            return false;
        const string prefix = "capture/responses/";
        const string suffix = ".jsonl";
        if (!path.StartsWith(prefix, StringComparison.Ordinal) ||
            !path.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var ordinalText = path.AsSpan(
            prefix.Length,
            path.Length - prefix.Length - suffix.Length);
        return int.TryParse(
                ordinalText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out shardOrdinal) &&
            shardOrdinal >= 0 &&
            shardOrdinal < MaximumResponseShards &&
            string.Equals(
                path,
                ResponseShardPath(shardOrdinal),
                StringComparison.Ordinal);
    }
}

public enum CapturePackageStatus
{
    Complete,
    Incomplete,
    Failed,
}

public enum CaptureScopeKind
{
    Solo,
    Band,
}

public enum CaptureCatalogSupportStatus
{
    Supported,
    Unsupported,
}

public enum CaptureResponseKind
{
    SoloLeaderboardPage,
    BandLeaderboardPage,
}

public enum CaptureRequestStatus
{
    Complete,
    Incomplete,
    Failed,
}

public enum CaptureScopeStatus
{
    Complete,
    Unsupported,
    Incomplete,
    Failed,
}

public enum CapturePackageFailureKind
{
    UnsupportedFormat,
    InvalidMetadata,
    InvalidHash,
    NegativeCount,
    RecordLimitExceeded,
    DuplicateOrdinal,
    MissingOrdinal,
    DuplicateScope,
    MissingScope,
    NonCanonicalOrder,
    IncompleteCapture,
    AggregateMismatch,
    EnvelopeMismatch,
    ArtifactMismatch,
    NonCanonicalJson,
    TierZeroVerificationFailed,
}

public sealed class CapturePackageException : InvalidOperationException
{
    public CapturePackageException(
        CapturePackageFailureKind kind,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public CapturePackageFailureKind Kind { get; }
}

public sealed record CaptureCatalogScopeSupport(
    CaptureScopeKind ScopeKind,
    string LeaderboardType,
    CaptureCatalogSupportStatus Status);

public sealed record CaptureCatalogSong(
    string SongId,
    IReadOnlyList<CaptureCatalogScopeSupport> ScopeSupport);

public sealed record CaptureCatalogArtifact(
    string FormatId,
    int SchemaVersion,
    long CatalogVersion,
    int SongCount,
    IReadOnlyList<CaptureCatalogSong> Songs);

public sealed record CaptureCatalogIdentity(
    long Version,
    int SchemaVersion,
    int SongCount,
    long Bytes,
    string ContentSha256);

public sealed record CaptureResponseArtifact(
    string FormatId,
    int SchemaVersion,
    int RequestOrdinal,
    int ScopeOrdinal,
    int ScopeRequestOrdinal,
    CaptureResponseKind ResponseKind,
    string SongId,
    CaptureScopeKind ScopeKind,
    string LeaderboardType,
    int PageIndex,
    int PageSize,
    int ProviderReportedTotalPages,
    long ProviderReportedTotalEntries,
    int EntryCount,
    IReadOnlyList<JsonElement> Entries);

public sealed record CaptureRequestDescriptor(
    int Ordinal,
    int ScopeOrdinal,
    int ScopeRequestOrdinal,
    string SongId,
    CaptureScopeKind ScopeKind,
    string LeaderboardType,
    int PageIndex,
    int PageSize,
    int ProviderReportedTotalPages,
    long ProviderReportedTotalEntries,
    int CapturedPageCount,
    long CapturedEntryCount,
    long CapturedRequestCount,
    long CapturedResponseBytes,
    CaptureRequestStatus Status,
    string ResponseShardPath,
    long ResponseOffset,
    int ResponseLength,
    string ContentSha256);

public sealed record CaptureScopeDescriptor(
    int Ordinal,
    string SongId,
    CaptureScopeKind ScopeKind,
    string LeaderboardType,
    int DeclaredPageCount,
    long DeclaredEntryCount,
    int ProviderReportedTotalPages,
    long ProviderReportedTotalEntries,
    int CapturedPageCount,
    long CapturedEntryCount,
    long CapturedRequestCount,
    long CapturedResponseBytes,
    CaptureScopeStatus Status,
    string ContentSha256);

public sealed record CaptureDescriptorSetReference(
    string Path,
    int SchemaVersion,
    int RecordCount,
    long Bytes,
    string Sha256);

public sealed record CapturePackageDefinition(
    string CaptureId,
    CaptureCatalogArtifact Catalog,
    IReadOnlyList<string> EnabledSoloInstruments,
    IReadOnlyList<string> EnabledBandTypes,
    DateTimeOffset CaptureStartedAtUtc,
    DateTimeOffset CaptureCompletedAtUtc,
    int TotalScopeCount,
    int TotalPageCount,
    long TotalEntryCount,
    long TotalRequestCount,
    long TotalResponseBytes,
    int ResponseShardCount,
    IReadOnlyList<CaptureRequestDescriptor> Requests,
    IReadOnlyList<CaptureScopeDescriptor> Scopes,
    CapturePackageStatus Status);

public sealed record CapturePackageManifest(
    string FormatId,
    int Version,
    string CaptureId,
    string ProviderId,
    string EnvelopeFormatId,
    int EnvelopeManifestVersion,
    string EnvelopePackageId,
    int EnvelopeAttempt,
    string ProducerIdentity,
    DateTimeOffset EnvelopeCreatedAtUtc,
    TierZeroSourceIdentity Source,
    TierZeroBuildIdentity Build,
    TierZeroDatabaseIdentity Database,
    TierZeroConfigurationFingerprint ScrapeConfiguration,
    IReadOnlyList<TierZeroParentRootHash> ParentRootHashes,
    CaptureCatalogIdentity Catalog,
    IReadOnlyList<string> EnabledSoloInstruments,
    IReadOnlyList<string> EnabledBandTypes,
    DateTimeOffset CaptureStartedAtUtc,
    DateTimeOffset CaptureCompletedAtUtc,
    int TotalScopeCount,
    int TotalPageCount,
    long TotalEntryCount,
    long TotalRequestCount,
    long TotalResponseBytes,
    int ResponseShardCount,
    CaptureDescriptorSetReference RequestPlan,
    CaptureDescriptorSetReference ScopeCompleteness,
    CapturePackageStatus Status,
    string? ManifestRootHash);

public sealed record CapturePackage(
    TierZeroEvidenceManifest Envelope,
    CapturePackageManifest Manifest,
    CaptureCatalogArtifact Catalog,
    IReadOnlyList<CaptureRequestDescriptor> Requests,
    IReadOnlyList<CaptureScopeDescriptor> Scopes);

public sealed record CapturePackageSealResult(
    TierZeroEvidenceManifest Envelope,
    CapturePackageManifest Manifest);

public sealed record CapturePackageStoragePolicy(
    long MaximumPackageBytes,
    long MinimumFreeSpaceReserveBytes,
    int MaximumRetainedSealedPackages);

public sealed record CapturePackageStorageState(
    long ProposedPackageBytes,
    long AvailableFreeSpaceBytes,
    int RetainedSealedPackageCount);

public enum CapturePackageAdmissionRejection
{
    PackageTooLarge,
    InsufficientFreeSpace,
    RetentionLimitReached,
}

public sealed record CapturePackageAdmissionDecision(
    bool IsAdmitted,
    IReadOnlyList<CapturePackageAdmissionRejection> Rejections,
    long CommittedAvailableFreeSpaceBytes,
    int CommittedRetainedSealedPackageCount,
    long? ProjectedRemainingFreeSpaceBytesIfAdmitted,
    int? ProjectedRetainedSealedPackageCountIfAdmitted);

public static class CapturePackageStorageAdmission
{
    public static CapturePackageAdmissionDecision Evaluate(
        CapturePackageStoragePolicy policy,
        CapturePackageStorageState state)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(state);
        if (policy.MaximumPackageBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                "Maximum package bytes must be positive.");
        }
        if (policy.MinimumFreeSpaceReserveBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                "Minimum free-space reserve cannot be negative.");
        }
        if (policy.MaximumRetainedSealedPackages <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                "Maximum retained sealed packages must be positive.");
        }
        if (state.ProposedPackageBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                "Proposed package bytes must be positive.");
        }
        if (state.AvailableFreeSpaceBytes < 0 ||
            state.RetainedSealedPackageCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                "Free-space and retained-package inputs cannot be negative.");
        }

        var rejections = new List<CapturePackageAdmissionRejection>();
        if (state.ProposedPackageBytes > policy.MaximumPackageBytes)
        {
            rejections.Add(
                CapturePackageAdmissionRejection.PackageTooLarge);
        }

        long? projectedRemainingFreeSpace =
            state.ProposedPackageBytes <= state.AvailableFreeSpaceBytes
                ? state.AvailableFreeSpaceBytes -
                  state.ProposedPackageBytes
                : null;
        if (projectedRemainingFreeSpace is null ||
            projectedRemainingFreeSpace <
            policy.MinimumFreeSpaceReserveBytes)
        {
            rejections.Add(
                CapturePackageAdmissionRejection.InsufficientFreeSpace);
        }

        int? projectedRetainedPackageCount =
            state.RetainedSealedPackageCount < int.MaxValue
                ? state.RetainedSealedPackageCount + 1
                : null;
        if (state.RetainedSealedPackageCount >=
            policy.MaximumRetainedSealedPackages)
        {
            rejections.Add(
                CapturePackageAdmissionRejection.RetentionLimitReached);
        }

        var admitted = rejections.Count == 0;
        return new CapturePackageAdmissionDecision(
            admitted,
            rejections,
            admitted
                ? projectedRemainingFreeSpace!.Value
                : state.AvailableFreeSpaceBytes,
            admitted
                ? projectedRetainedPackageCount!.Value
                : state.RetainedSealedPackageCount,
            projectedRemainingFreeSpace,
            projectedRetainedPackageCount);
    }
}
