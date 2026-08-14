namespace FSTService.Scraping.Replay;

public static class TierZeroEvidenceFormat
{
    public const string FormatId = "fst.tier0.evidence";
    public const int ManifestVersion = 1;
    public const string ManifestFileName = "manifest.json";
    public const string ChecksumFileName = "checksums.sha256";
    internal const string StateFileName = "package-state.json";
    internal const string LockFileName = "package.lock";
}

public enum TierZeroPackageStatus
{
    Draft,
    Interrupted,
    Sealed,
    Failed,
}

public sealed record TierZeroCatalogIdentity(
    string Identity,
    string ContentSha256);

public sealed record TierZeroSourceIdentity(
    long? ScrapeId,
    long? PublicationId,
    DateTimeOffset? SourceCutUtc,
    TierZeroCatalogIdentity Catalog);

public sealed record TierZeroBuildIdentity(
    string GitCommit,
    string OciImageDigest,
    string OciImageRevision,
    string ServiceVersion);

public sealed record TierZeroDatabaseIdentity(
    int MajorVersion,
    IReadOnlyList<string> Extensions,
    string SchemaFingerprint);

public sealed record TierZeroConfigurationFingerprint(
    string Algorithm,
    IReadOnlyList<string> Keys,
    string ValuesSha256);

public sealed record TierZeroPhaseDescriptor(
    string Id,
    string Label,
    string LegacyPhase,
    int Ordinal,
    string? TrackerOperation,
    string? BranchId,
    string? OperationKey,
    string? DefaultUnitsKind);

public sealed record TierZeroPhasePlan(
    string Id,
    string Version,
    IReadOnlyList<TierZeroPhaseDescriptor> Phases)
{
    public static TierZeroPhasePlan FromCurrentCatalog() =>
        new(
            PhaseProgressCatalog.OperationId,
            PhaseProgressCatalog.PlanVersion,
            PhaseProgressCatalog.All
                .OrderBy(static phase => phase.Ordinal)
                .ThenBy(static phase => phase.Id, StringComparer.Ordinal)
                .Select(static phase => new TierZeroPhaseDescriptor(
                    phase.Id,
                    phase.Label,
                    phase.LegacyPhase,
                    phase.Ordinal,
                    phase.TrackerOperation,
                    phase.BranchId,
                    phase.OperationKey,
                    phase.DefaultUnitsKind))
                .ToArray());
}

public sealed record TierZeroArtifactRange(
    string Field,
    string? Minimum,
    string? Maximum);

public sealed record TierZeroArtifactDescriptor(
    string LogicalOwner,
    string Path,
    string MediaType,
    int SchemaVersion,
    long RowCount,
    IReadOnlyList<TierZeroArtifactRange> Ranges,
    long CompressedBytes,
    long UncompressedBytes,
    string Sha256);

public sealed record TierZeroArtifactRegistration(
    string LogicalOwner,
    string Path,
    string MediaType,
    int SchemaVersion,
    long RowCount,
    long UncompressedBytes,
    IReadOnlyList<TierZeroArtifactRange>? Ranges = null);

public sealed record TierZeroSummaryReference(
    string LogicalOwner,
    string Path,
    string Sha256,
    long? RecordCount);

public sealed record TierZeroSummaryReferences(
    IReadOnlyList<TierZeroSummaryReference> ScopeManifests,
    IReadOnlyList<TierZeroSummaryReference> ScopeFingerprints,
    IReadOnlyList<TierZeroSummaryReference> PhaseOutcomes,
    IReadOnlyList<TierZeroSummaryReference> PhaseTimings)
{
    public static TierZeroSummaryReferences Empty { get; } =
        new([], [], [], []);
}

public sealed record TierZeroParentRootHash(
    string LogicalParent,
    string Sha256);

public sealed record TierZeroChecksumManifest(
    string Path,
    string Algorithm,
    int EntryCount,
    string Sha256);

public sealed record TierZeroPackageDraft(
    string PackageId,
    TierZeroSourceIdentity Source,
    TierZeroBuildIdentity Build,
    TierZeroDatabaseIdentity Database,
    TierZeroConfigurationFingerprint Configuration,
    TierZeroSummaryReferences SummaryReferences,
    IReadOnlyList<TierZeroParentRootHash> ParentRootHashes,
    int Attempt,
    string ProducerIdentity,
    DateTimeOffset CreatedAtUtc);

public sealed record TierZeroEvidenceManifest(
    string FormatId,
    int ManifestVersion,
    string PackageId,
    TierZeroSourceIdentity Source,
    TierZeroBuildIdentity Build,
    TierZeroDatabaseIdentity Database,
    TierZeroConfigurationFingerprint Configuration,
    TierZeroPhasePlan PhasePlan,
    TierZeroSummaryReferences SummaryReferences,
    IReadOnlyList<TierZeroArtifactDescriptor> Artifacts,
    IReadOnlyList<TierZeroParentRootHash> ParentRootHashes,
    int Attempt,
    string ProducerIdentity,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset SealedAtUtc,
    TierZeroPackageStatus Status,
    string? Error,
    string StateSha256,
    TierZeroChecksumManifest ChecksumManifest,
    string? PackageRootHash);

public sealed record TierZeroResumeExpectations(
    string PackageId,
    int Attempt,
    string ProducerIdentity,
    IReadOnlyList<TierZeroParentRootHash> ParentRootHashes,
    string ConfigurationValuesSha256,
    string DatabaseSchemaFingerprint,
    string PhasePlanId,
    string PhasePlanVersion);

public sealed record TierZeroVerificationExpectations(
    IReadOnlyList<TierZeroParentRootHash>? ParentRootHashes = null,
    string? ConfigurationValuesSha256 = null,
    string? DatabaseSchemaFingerprint = null,
    string? PhasePlanId = null,
    string? PhasePlanVersion = null);

internal sealed record TierZeroPackageState(
    TierZeroPackageDraft Draft,
    TierZeroPhasePlan PhasePlan,
    IReadOnlyList<TierZeroArtifactDescriptor> Artifacts,
    TierZeroPendingArtifact? PendingArtifact,
    TierZeroPackageStatus Status,
    string? Error,
    DateTimeOffset? InterruptedAtUtc);

internal sealed record TierZeroPendingArtifact(
    TierZeroArtifactDescriptor Descriptor,
    string TemporaryPath);
