namespace FSTService.Persistence.Maintenance;

public static class SnapshotGenerationRetentionContract
{
    public const int PlannerVersion = 1;
    public const int ConfigVersion = 1;
    public const long PlannerAdvisoryLockKey = 2026082301;
    public const string PostPublicationSafePoint = "post_publication";

    public static readonly IReadOnlyList<SnapshotGenerationRetentionInstrument>
        Instruments =
        [
            new(
                "Solo_Guitar",
                "leaderboard_entries_snapshot_solo_guitar",
                0),
            new(
                "Solo_Bass",
                "leaderboard_entries_snapshot_solo_bass",
                1),
            new(
                "Solo_Vocals",
                "leaderboard_entries_snapshot_solo_vocals",
                2),
            new(
                "Solo_Drums",
                "leaderboard_entries_snapshot_solo_drums",
                3),
            new(
                "Solo_PeripheralGuitar",
                "leaderboard_entries_snapshot_pro_guitar",
                4),
            new(
                "Solo_PeripheralBass",
                "leaderboard_entries_snapshot_pro_bass",
                5),
            new(
                "Solo_PeripheralVocals",
                "leaderboard_entries_snapshot_pro_vocals",
                6),
            new(
                "Solo_PeripheralCymbals",
                "leaderboard_entries_snapshot_pro_cymbals",
                7),
            new(
                "Solo_PeripheralDrums",
                "leaderboard_entries_snapshot_pro_drums",
                8),
        ];
}

public static class SnapshotGenerationRetentionCycleStatus
{
    public const string Planning = "planning";
    public const string Observed = "observed";
    public const string Planned = "planned";
    public const string Blocked = "blocked";
    public const string Deferred = "deferred";
    public const string Failed = "failed";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string SafetyFailed = "safety_failed";
}

public static class SnapshotGenerationRetentionJobStatus
{
    public const string Observed = "observed";
    public const string Planned = "planned";
    public const string Blocked = "blocked";
    public const string Deferred = "deferred";
    public const string Leased = "leased";
    public const string Executing = "executing";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string SafetyFailed = "safety_failed";

    public static readonly string[] ScrapeAdmissionBlockingStatuses =
    [
        Leased,
        Executing,
        SafetyFailed,
    ];
}

public static class SnapshotGenerationRetentionOperationKind
{
    public const string DropWholeChild = "drop_whole_child";
    public const string CompactSparseChild = "compact_sparse_child";
}

public enum SnapshotGenerationRetentionPlanDisposition
{
    Disabled,
    Existing,
    Busy,
    Observed,
    Planned,
    Blocked,
    Deferred,
    Failed,
}

public sealed record SnapshotGenerationRetentionInstrument(
    string Instrument,
    string RootRelation,
    int FairnessOrder)
{
    public string DefaultRelation => $"{RootRelation}_default";
}

public sealed record SnapshotGenerationRetentionPlanRequest(
    long TriggerScrapeId,
    long TriggerPublicationId,
    DateTime SafePointAtUtc,
    string SafePointKind =
        SnapshotGenerationRetentionContract.PostPublicationSafePoint);

public sealed record SnapshotGenerationRetentionPolicy(
    bool ReportOnly,
    int NewestGenerationsToKeep,
    int MinimumLaterSuccessfulPublications,
    int MaxPlannedChildrenPerCycle,
    bool BlockUnreplayedWriterFailures);

public sealed record SnapshotGenerationRetentionPlanResult(
    SnapshotGenerationRetentionPlanDisposition Disposition,
    long? CycleId,
    string Reason,
    string? PlanDigest,
    int CandidateCount,
    int PlannedCount,
    int BlockedCount,
    long CandidateBytes,
    long BlockedBytes)
{
    public static SnapshotGenerationRetentionPlanResult Disabled() =>
        new(
            SnapshotGenerationRetentionPlanDisposition.Disabled,
            null,
            "snapshot-generation retention planning is disabled",
            null,
            0,
            0,
            0,
            0,
            0);
}

public sealed record SnapshotGenerationRetentionCycle(
    long CycleId,
    long TriggerScrapeId,
    long TriggerPublicationId,
    string SafePointKind,
    DateTime SafePointAtUtc,
    int PlannerVersion,
    int ConfigVersion,
    bool ReportOnly,
    string? PlanDigest,
    string Status,
    int CandidateCount,
    int BlockedCount,
    long CandidateBytes,
    long BlockedBytes,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    string? ErrorMessage,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record SnapshotGenerationRetentionJob(
    long JobId,
    long CycleId,
    bool ReportOnly,
    string OperationKind,
    string Instrument,
    string RootRelation,
    string ChildRelation,
    long SnapshotId,
    long ChildOid,
    long ChildRelfilenode,
    string PartitionBound,
    string TablespaceName,
    long RowEstimate,
    long TotalBytes,
    string ProtectedEvidenceJson,
    string ReferenceEvidenceJson,
    IReadOnlyList<string> BlockerCodes,
    string BlockerDetailsJson,
    string Status,
    int AttemptCount,
    string? LeaseOwner,
    Guid? LeaseToken,
    DateTime? LeaseAcquiredAtUtc,
    DateTime? LeaseExpiresAtUtc,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    string? ErrorMessage,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record SnapshotGenerationRetentionEvidence(
    long EvidenceId,
    long CycleId,
    long? JobId,
    int Sequence,
    string Phase,
    string Kind,
    string PayloadJson,
    string? PreviousHash,
    string CurrentHash,
    DateTime CreatedAtUtc);

internal sealed record SnapshotGenerationRetentionJobDraft(
    bool ReportOnly,
    string OperationKind,
    string Instrument,
    string RootRelation,
    string ChildRelation,
    long SnapshotId,
    long ChildOid,
    long ChildRelfilenode,
    string PartitionBound,
    string TablespaceName,
    long RowEstimate,
    long TotalBytes,
    string ProtectedEvidenceJson,
    string ReferenceEvidenceJson,
    IReadOnlyList<string> BlockerCodes,
    string BlockerDetailsJson,
    string Status);
