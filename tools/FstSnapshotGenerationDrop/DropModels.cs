using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FstSnapshotGenerationEvidence;
using FstSnapshotGenerationQuarantine;

namespace FstSnapshotGenerationDrop;

public static class SnapshotGenerationDropToolContract
{
    public const int SchemaVersion = 1;
    public const string ToolId =
        "fst.snapshot-generation-drop-only.v1";
    public const long DropAdvisoryLockKey =
        2026083002L;
    public const int MinimumSoakSeconds = 1800;
    public const int MinimumHealthSamples = 60;
    public const int HealthSampleIntervalSeconds = 30;
}

public static class DropJson
{
    public static readonly JsonSerializerOptions Strict =
        new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition =
                JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling =
                JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true,
        };

    public static byte[] Canonical<T>(T value) =>
        SnapshotGenerationCanonicalJson.Serialize(value);

    public static string Sha256<T>(T value) =>
        Convert.ToHexString(
                SHA256.HashData(Canonical(value)))
            .ToLowerInvariant();
}

public sealed record SnapshotGenerationDropCandidate(
    long CycleId,
    long ObservationId,
    long TriggerScrapeId,
    long TriggerPublicationId,
    string Instrument,
    long SnapshotId,
    string RootRelation,
    long RootOid,
    string ChildRelation,
    long ChildOid,
    long ChildRelfilenode,
    long RowCount,
    long TotalBytes,
    string StableChildIdentityHash,
    string StableConfigSchemaHash);

public sealed record SnapshotGenerationHealthEvidence(
    int SchemaVersion,
    string ToolId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int SampleIntervalSeconds,
    int SuccessfulSampleCount,
    long PublicationId,
    long PublishedScrapeId,
    bool AllHealthy,
    IReadOnlyList<SnapshotGenerationHealthSample> Samples,
    string? EvidenceSha256)
{
    public SnapshotGenerationHealthEvidence Seal() =>
        this with
        {
            EvidenceSha256 = DropJson.Sha256(
                this with { EvidenceSha256 = null }),
        };

    public void Validate()
    {
        if (SchemaVersion != 1
            || ToolId !=
                "fst.snapshot-generation-drop-health.v1"
            || !AllHealthy
            || SampleIntervalSeconds !=
                SnapshotGenerationDropToolContract
                    .HealthSampleIntervalSeconds
            || SuccessfulSampleCount <
                SnapshotGenerationDropToolContract
                    .MinimumHealthSamples
            || Samples.Count != SuccessfulSampleCount
            || CompletedAtUtc - StartedAtUtc <
                TimeSpan.FromSeconds(
                    SnapshotGenerationDropToolContract
                        .MinimumSoakSeconds)
            || Samples.Count == 0
            || Samples[0].CapturedAtUtc <
                StartedAtUtc
            || Samples[^1].CapturedAtUtc >
                CompletedAtUtc
            || Samples.Any(static sample =>
                !sample.Ready
                || !sample.ApiHealthy
                || sample.LockWaiterCount != 0
                || sample.PublicReadsFrozen
                || sample.RunningScrapeCount != 0)
            || Samples.Any(sample =>
                sample.PublicationId != PublicationId
                || sample.PublishedScrapeId !=
                    PublishedScrapeId)
            || Samples
                .Zip(
                    Samples.Skip(1),
                    static (left, right) =>
                        right.CapturedAtUtc
                        - left.CapturedAtUtc)
                .Any(interval =>
                    interval <
                        TimeSpan.FromSeconds(25)
                    || interval >
                        TimeSpan.FromSeconds(60)))
        {
            throw new InvalidDataException(
                "Drop health evidence is incomplete or unsafe.");
        }
        var sealedEvidence = Seal();
        if (!string.Equals(
                EvidenceSha256,
                sealedEvidence.EvidenceSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Drop health evidence digest is invalid.");
        }
    }
}

public sealed record SnapshotGenerationHealthSample(
    DateTimeOffset CapturedAtUtc,
    long PublicationId,
    long PublishedScrapeId,
    bool Ready,
    bool ApiHealthy,
    bool PublicReadsFrozen,
    int RunningScrapeCount,
    int LockWaiterCount);

public sealed record SnapshotGenerationDropDatabaseSnapshot(
    DateTimeOffset CapturedAtUtc,
    string DatabaseName,
    long DatabaseOid,
    string SystemIdentifier,
    int ServerVersionNum,
    int BackendPid,
    long LatestCycleId,
    long CurrentPublicationId,
    long PublishedScrapeId,
    bool PublicReadsFrozen,
    long? WorkingPublicationId,
    bool PublicationCommitIntentActive,
    bool MaxScoreMutationGateActive,
    bool NotificationsComplete,
    int RunningScrapeCount,
    int ActiveReferenceCount,
    int UnreplayedWriterFailureCount,
    int OtherActiveHoldCount,
    bool ExactHoldActive,
    bool PrivateRelationExists,
    bool OriginalRelationAbsent,
    long CurrentChildOid,
    long CurrentChildRelfilenode,
    long CurrentRowCount,
    long CurrentTotalBytes,
    bool Detached,
    bool ExactCheckPresent,
    bool MutationGuardPresent,
    bool DefaultIdentityValid,
    bool DefaultExclusionPresent,
    long DefaultRowCount,
    int ChildIndexCount,
    bool WorkerOffline,
    JsonElement DependencyInventory,
    string DependencyInventorySha256,
    JsonElement TopologyEvidence,
    string TopologySha256,
    JsonElement LivenessEvidence,
    string LivenessSha256);

public sealed record SnapshotGenerationSemanticIndexEvidence(
    string Role,
    long IndexOid,
    long IndexRelfilenode,
    bool Primary,
    bool Unique,
    bool Valid,
    bool Ready,
    string AccessMethod,
    string TablespaceName,
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<string> SortDirections,
    IReadOnlyList<string> NullsOrder,
    IReadOnlyList<string> Opclasses,
    IReadOnlyList<string> Collations,
    string? Expressions,
    string? Predicate,
    long ParentRootIndexOid,
    long ParentTopIndexOid,
    string ParentTopRole);

public sealed record SnapshotGenerationArchiveSemanticEvidence(
    int ProjectionVersion,
    string CatalogSha256,
    string SemanticCatalogSha256,
    string LogicalIndexShapeSha256,
    string PhysicalIndexInventorySha256,
    IReadOnlyList<SnapshotGenerationSemanticIndexEvidence>
        Indexes);

public sealed record SnapshotGenerationDropPlan(
    int SchemaVersion,
    string ToolId,
    DateTimeOffset GeneratedAtUtc,
    bool ExplicitApprovalRequired,
    SnapshotGenerationQuarantinePlan RehearsalPlan,
    SnapshotGenerationQuarantinePlan ActivePlan,
    SnapshotGenerationQuarantineExecutionReport
        RehearsalQuarantineReport,
    SnapshotGenerationQuarantineExecutionReport
        RehearsalReattachReport,
    SnapshotGenerationQuarantineExecutionReport
        ActiveQuarantineReport,
    SnapshotGenerationQuarantineAttestationReport
        RehearsalQuarantinedAttestation,
    SnapshotGenerationQuarantineAttestationReport
        RehearsalSoakAttestation,
    SnapshotGenerationQuarantineAttestationReport
        RehearsalReattachedAttestation,
    SnapshotGenerationQuarantineAttestationReport
        ActiveQuarantinedAttestation,
    SnapshotGenerationQuarantineAttestationReport
        ActiveSoakAttestation,
    SnapshotGenerationArchiveSemanticEvidence
        RehearsalSemantic,
    SnapshotGenerationArchiveSemanticEvidence
        ActiveSemantic,
    RouteParityEvidence PreDropParity,
    SnapshotGenerationHealthEvidence Health,
    SnapshotGenerationDropDatabaseSnapshot Database,
    string RecoveryBundlePath,
    string RecoveryBundleManifestSha256,
    long RequiredCapacityBytes,
    long CapacityReserveBytes,
    string BinaryPath,
    string BinarySha256,
    string RestoreToolPath,
    string RestoreToolSha256,
    string RestoreImageIdSha256,
    string RepositoryCommit,
    string FreshProofManifestPath,
    string FreshProofManifestSha256,
    DateTimeOffset ProofCompletedAtUtc,
    string? PlanDigest,
    string? DropOperationId)
{
    public SnapshotGenerationDropPlan WithoutIdentity() =>
        this with
        {
            PlanDigest = null,
            DropOperationId = null,
        };

    public SnapshotGenerationDropPlan Seal()
    {
        var digest = DropJson.Sha256(WithoutIdentity());
        var operationId = DropJson.Sha256(
            new
            {
                ToolId =
                    SnapshotGenerationDropToolContract.ToolId,
                PlanDigest = digest,
            })[..32];
        return this with
        {
            PlanDigest = digest,
            DropOperationId = operationId,
        };
    }

    public void Validate()
    {
        RehearsalPlan.Validate();
        ActivePlan.Validate();
        Health.Validate();
        if (SchemaVersion !=
                SnapshotGenerationDropToolContract.SchemaVersion
            || ToolId !=
                SnapshotGenerationDropToolContract.ToolId
            || !ExplicitApprovalRequired
            || string.IsNullOrWhiteSpace(PlanDigest)
            || string.IsNullOrWhiteSpace(DropOperationId)
            || string.IsNullOrWhiteSpace(
                RecoveryBundleManifestSha256)
            || string.IsNullOrWhiteSpace(BinarySha256)
            || string.IsNullOrWhiteSpace(RestoreToolSha256)
            || string.IsNullOrWhiteSpace(
                RestoreImageIdSha256)
            || string.IsNullOrWhiteSpace(
                FreshProofManifestPath)
            || RequiredCapacityBytes <= 0
            || CapacityReserveBytes < 0
            || RehearsalSemantic.ProjectionVersion != 1
            || ActiveSemantic.ProjectionVersion != 1
            || !IsSha256(
                RehearsalSemantic.CatalogSha256)
            || !IsSha256(
                ActiveSemantic.CatalogSha256)
            || !IsSha256(
                RehearsalSemantic
                    .SemanticCatalogSha256)
            || !IsSha256(
                ActiveSemantic.SemanticCatalogSha256)
            || !IsSha256(
                RehearsalSemantic
                    .LogicalIndexShapeSha256)
            || !IsSha256(
                ActiveSemantic.LogicalIndexShapeSha256)
            || !IsSha256(
                RehearsalSemantic
                    .PhysicalIndexInventorySha256)
            || !IsSha256(
                ActiveSemantic
                    .PhysicalIndexInventorySha256)
            || RehearsalSemantic.Indexes.Count != 2
            || ActiveSemantic.Indexes.Count != 2
            || !IsSha256(RecoveryBundleManifestSha256)
            || !IsSha256(BinarySha256)
            || !IsSha256(RestoreToolSha256)
            || !IsSha256(RestoreImageIdSha256)
            || !IsSha256(FreshProofManifestSha256)
            || RepositoryCommit.Length != 40
            || RepositoryCommit.Any(character =>
                character is not (
                    >= '0' and <= '9'
                    or >= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                "Snapshot-generation drop plan contract is invalid.");
        }
        var expected = Seal();
        if (!string.Equals(
                expected.PlanDigest,
                PlanDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                expected.DropOperationId,
                DropOperationId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Snapshot-generation drop plan digest or operation ID is invalid.");
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64
        && value.All(character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f');
}

public sealed record SnapshotGenerationDropExecutionReport(
    int SchemaVersion,
    string ToolId,
    string Action,
    string DropOperationId,
    string PlanDigest,
    string Status,
    string CommitOutcome,
    DateTimeOffset CompletedAtUtc,
    string Actor,
    string Reference,
    string Instrument,
    long SnapshotId,
    long ChildOid,
    long ChildRelfilenode,
    long RowCount,
    string RowFingerprintSha256,
    JsonElement Evidence,
    string? ReportSha256 = null)
{
    public SnapshotGenerationDropExecutionReport Seal() =>
        this with
        {
            ReportSha256 = DropJson.Sha256(
                this with { ReportSha256 = null }),
        };
}

public sealed record SnapshotGenerationDropAttestationReport(
    int SchemaVersion,
    string ToolId,
    string DropOperationId,
    string Stage,
    long? AttestationId,
    DateTimeOffset CompletedAtUtc,
    string Actor,
    RouteParityEvidence Parity,
    JsonElement DatabaseEvidence,
    string EvidenceSha256,
    string? ReportSha256 = null)
{
    public SnapshotGenerationDropAttestationReport Seal() =>
        this with
        {
            ReportSha256 = DropJson.Sha256(
                this with { ReportSha256 = null }),
        };
}

public sealed record SnapshotGenerationDropState(
    bool OperationExists,
    string? DropOperationId,
    string? PlanDigest,
    string? Instrument,
    long? SnapshotId,
    string? ChildSchema,
    string? ChildRelation,
    long? ChildOid,
    long? ChildRelfilenode,
    string? QuarantineSchema,
    string? QuarantineRelation,
    bool OriginalRelationExists,
    bool QuarantineRelationExists,
    bool OriginalOidExists,
    bool DurableDefaultExclusionPresent,
    bool HoldActive,
    bool Restored,
    string? ApprovedBy,
    string? ApprovalReference);
