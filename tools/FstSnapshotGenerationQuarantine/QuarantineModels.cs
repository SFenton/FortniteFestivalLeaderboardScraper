using System.Text.Json;
using System.Text.Json.Serialization;
using FstSnapshotGenerationEvidence;

namespace FstSnapshotGenerationQuarantine;

public static class QuarantineJson
{
    public static readonly JsonSerializerOptions Strict =
        CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(
            JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition =
                JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling =
                JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true,
        };
        return options;
    }

    public static byte[] Canonical<T>(T value) =>
        SnapshotGenerationCanonicalJson.Serialize(value);

    public static string Sha256<T>(T value) =>
        Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    Canonical(value)))
            .ToLowerInvariant();
}

public sealed record ArchivePackageEvidence(
    string PackagePath,
    string PackageManifestSha256,
    string ArchiveSha256,
    string ProofManifestPath,
    string ProofManifestSha256,
    long CycleId,
    long TriggerScrapeId,
    long TriggerPublicationId,
    string CandidateIdentityHash,
    string ObservationHash,
    long ObservationId,
    string Instrument,
    long SnapshotId,
    string RootSchema,
    string RootRelation,
    long RootOid,
    string ChildSchema,
    string ChildRelation,
    long ChildOid,
    long ChildRelfilenode,
    string StableChildIdentityHash,
    string StableConfigSchemaHash,
    long RowCount,
    string RowFingerprintSha256,
    string LogicalCatalogSha256,
    long TotalBytes,
    string DatabaseName,
    long DatabaseOid,
    string SystemIdentifier,
    int ServerVersionNum);

public sealed record SourceScrapeEvidence(
    string ManifestPath,
    string ManifestSha256,
    long ScrapeId,
    long PublishedScrapeId,
    int SongCount,
    long TotalEntries,
    long ScopeCount,
    long PublishedScopeCount,
    long PublishedRowCount);

public sealed record RouteParityEvidence(
    string BaselineManifestPath,
    string BaselineManifestSha256,
    string CandidateManifestPath,
    string CandidateManifestSha256,
    long PublicationId,
    long PublishedScrapeId,
    int RouteCount,
    bool StatusParity,
    bool SemanticJsonParity,
    int DifferenceCount);

public sealed record QuarantineDatabaseSnapshot(
    DateTimeOffset CapturedAtUtc,
    string DatabaseName,
    long DatabaseOid,
    string SystemIdentifier,
    int ServerVersionNum,
    string CurrentUser,
    long CurrentPublicationId,
    long PublishedScrapeId,
    bool PublicReadsFrozen,
    long? WorkingPublicationId,
    bool PublicationCommitIntentActive,
    bool MaxScoreMutationGateActive,
    bool NotificationsComplete,
    bool TriggerScrapeCompleted,
    bool TriggerPublicationCurrent,
    long LatestCycleId,
    long CycleTriggerScrapeId,
    long CycleTriggerPublicationId,
    string CycleStatus,
    bool ReportOnly,
    bool OracleAgreement,
    int CandidateCount,
    int BlockedCount,
    int GlobalBlockerCount,
    bool PlannerOracleSetsEqual,
    string CandidateIdentityHash,
    string ObservationHash,
    long ObservationId,
    string Instrument,
    long SnapshotId,
    string RootRelation,
    long RootOid,
    string ChildRelation,
    long ChildOid,
    long ChildRelfilenode,
    string PartitionBound,
    string StableChildIdentityHash,
    string StableConfigSchemaHash,
    string Classification,
    bool PlannerLive,
    bool OracleLive,
    int BlockerCount,
    long CurrentRowCount,
    long CurrentTotalBytes,
    int RunningScrapeCount,
    int ActiveHoldCount,
    int UnreplayedWriterFailureCount,
    int AcceptedRecentCycleCount,
    int AcceptedRecentPublicationCount,
    int AcceptedRecentCandidateIdentityCount);

public sealed record SnapshotGenerationQuarantinePlan(
    int SchemaVersion,
    string ToolId,
    DateTimeOffset GeneratedAtUtc,
    ArchivePackageEvidence Archive,
    SourceScrapeEvidence SourceScrape,
    RouteParityEvidence PreQuarantineParity,
    QuarantineDatabaseSnapshot Database,
    bool ExplicitApprovalRequired,
    string? PlanDigest,
    string? OperationId)
{
    public SnapshotGenerationQuarantinePlan WithoutIdentity() =>
        this with
        {
            PlanDigest = null,
            OperationId = null,
        };

    public SnapshotGenerationQuarantinePlan Seal()
    {
        var digest = QuarantineJson.Sha256(
            WithoutIdentity());
        var operationId = QuarantineJson.Sha256(
            new
            {
                ToolId =
                    SnapshotGenerationQuarantineEvidenceContract
                        .ToolId,
                PlanDigest = digest,
            })[..32];
        return this with
        {
            PlanDigest = digest,
            OperationId = operationId,
        };
    }

    public void Validate()
    {
        if (SchemaVersion !=
                SnapshotGenerationQuarantineEvidenceContract
                    .SchemaVersion
            || ToolId !=
                SnapshotGenerationQuarantineEvidenceContract
                    .ToolId
            || !ExplicitApprovalRequired
            || string.IsNullOrWhiteSpace(PlanDigest)
            || string.IsNullOrWhiteSpace(OperationId))
        {
            throw new InvalidDataException(
                "Quarantine plan contract is invalid.");
        }

        var expected = Seal();
        if (!string.Equals(
                expected.PlanDigest,
                PlanDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                expected.OperationId,
                OperationId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Quarantine plan digest or operation ID is invalid.");
        }
    }
}

public sealed record SnapshotGenerationQuarantineExecutionReport(
    int SchemaVersion,
    string ToolId,
    string Action,
    string OperationId,
    string PlanDigest,
    string Status,
    DateTimeOffset CompletedAtUtc,
    string Actor,
    string Reference,
    string DatabaseName,
    string SystemIdentifier,
    long PublicationId,
    long PublishedScrapeId,
    string Instrument,
    long SnapshotId,
    string ChildRelation,
    string? QuarantineRelation,
    long ChildOid,
    long ChildRelfilenode,
    long RowCount,
    string RowFingerprintSha256,
    JsonElement Evidence,
    string? ReportSha256 = null)
{
    public SnapshotGenerationQuarantineExecutionReport Seal() =>
        this with
        {
            ReportSha256 = QuarantineJson.Sha256(
                this with { ReportSha256 = null }),
        };
}

public sealed record SnapshotGenerationQuarantineAttestationReport(
    int SchemaVersion,
    string ToolId,
    string OperationId,
    string PlanDigest,
    string Stage,
    long AttestationId,
    DateTimeOffset CompletedAtUtc,
    string Actor,
    RouteParityEvidence Parity,
    JsonElement DatabaseEvidence,
    string EvidenceSha256,
    string? ReportSha256 = null)
{
    public SnapshotGenerationQuarantineAttestationReport Seal() =>
        this with
        {
            ReportSha256 = QuarantineJson.Sha256(
                this with { ReportSha256 = null }),
        };
}

public sealed record QuarantineOperationState(
    string OperationId,
    string PlanDigest,
    long TriggerPublicationId,
    long TriggerScrapeId,
    string Instrument,
    long SnapshotId,
    string RootSchema,
    string RootRelation,
    long RootOid,
    string ChildSchema,
    string ChildRelation,
    long ChildOid,
    long ChildRelfilenode,
    string QuarantineSchema,
    string QuarantineRelation,
    string SnapshotCheckConstraint,
    string MutationGuardTrigger,
    string DefaultPartitionSchema,
    string DefaultPartitionRelation,
    long DefaultPartitionOid,
    string DefaultExclusionConstraint,
    long RowCount,
    string RowFingerprintSha256,
    long CurrentPublicationId,
    long CurrentPublishedScrapeId,
    bool CurrentPublicReadsFrozen,
    int RunningScrapeCount,
    long TargetReferenceCount,
    string? LatestSuccessfulSoakCandidateManifestSha256,
    bool Reattached,
    string? CurrentSchema,
    string? CurrentRelation,
    long? CurrentOid,
    long? CurrentRelfilenode,
    long? CurrentParentOid,
    string? CurrentPartitionBound,
    bool ExactCheckPresent,
    bool MutationGuardPresent,
    bool DefaultExclusionPresent,
    int SuccessfulQuarantinedAttestations,
    int SuccessfulSoakAttestations,
    int SuccessfulReattachedAttestations);

public sealed record FingerprintEvidence(
    string Algorithm,
    string Sha256,
    long RowCount,
    long StreamBytes);
