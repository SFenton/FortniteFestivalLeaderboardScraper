using FSTService.Scraping.Replay;

namespace FSTService.Persistence.Maintenance;

public static class SnapshotGenerationRetentionContract
{
    public const int PlannerVersion = 3;
    public const int ConfigVersion = 1;
    public const long PlannerAdvisoryLockKey = 2026082301;
    public const string TerminalWorkerSafePoint =
        "terminal_worker_post_publication";
    public static readonly IReadOnlySet<string>
        RequiredSnapshotParentIndexNames =
        new HashSet<string>(
        [
            "leaderboard_entries_snapshot_pkey",
            "ix_les_snapshot_song_score",
        ], StringComparer.Ordinal);

    public static readonly IReadOnlyList<
        SnapshotGenerationRetentionInstrument> Instruments =
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

internal static class SnapshotGenerationRetentionLockOrder
{
    internal static readonly IReadOnlyList<long> OrderedKeys =
    [
        RegistrationMutationGate.AdvisoryLockKey,
        ServiceMaintenanceLock.AdvisoryLockKey,
        PublicationGenerationSchema.AdvisoryLockKey,
        SnapshotGenerationRetentionContract.PlannerAdvisoryLockKey,
    ];
}

public static class SnapshotGenerationRetentionCycleStatus
{
    public const string Observed = "observed";
    public const string Blocked = "blocked";
    public const string OracleMismatch = "oracle_mismatch";
    public const string Failed = "failed";
}

public static class SnapshotGenerationRetentionClassification
{
    public const string Candidate = "candidate";
    public const string Protected = "protected";
    public const string Blocked = "blocked";
    public const string OracleMismatch = "oracle_mismatch";
}

public enum SnapshotGenerationRetentionPlanDisposition
{
    Disabled,
    Existing,
    Deferred,
    Observed,
    Blocked,
    OracleMismatch,
    Failed,
}

public sealed record SnapshotGenerationRetentionInstrument(
    string Instrument,
    string RootRelation,
    int CanonicalOrder)
{
    public string DefaultRelation => $"{RootRelation}_default";
}

public sealed record SnapshotGenerationRetentionPlanRequest(
    long TriggerScrapeId,
    long TriggerPublicationId,
    DateTime SafePointAtUtc,
    long? BroadcastCompletedScrapeId,
    bool BackgroundWorkQuiesced,
    string SafePointKind =
        SnapshotGenerationRetentionContract.TerminalWorkerSafePoint);

public sealed record SnapshotGenerationRetentionPlanResult(
    SnapshotGenerationRetentionPlanDisposition Disposition,
    long? CycleId,
    string Reason,
    string? CandidateIdentityHash,
    string? ObservationHash,
    int CandidateCount,
    int ProtectedCount,
    int BlockedCount,
    long CandidateBytes,
    bool OracleAgreement,
    bool Retryable)
{
    public static SnapshotGenerationRetentionPlanResult Disabled() =>
        new(
            SnapshotGenerationRetentionPlanDisposition.Disabled,
            null,
            "snapshot-generation retention report-only planning is disabled",
            null,
            null,
            0,
            0,
            0,
            0,
            OracleAgreement: false,
            Retryable: false);
}

public sealed record SnapshotGenerationRetentionCycle(
    long CycleId,
    long TriggerScrapeId,
    long TriggerPublicationId,
    string SafePointKind,
    DateTime SafePointAtUtc,
    int PlannerVersion,
    int ConfigVersion,
    string Status,
    bool OracleAgreement,
    string CandidateIdentityHash,
    string ObservationHash,
    string PlannerChildSetJson,
    string PlannerLiveSetJson,
    string PlannerCandidateSetJson,
    string OracleChildSetJson,
    string OracleLiveSetJson,
    string OracleCandidateSetJson,
    int CandidateCount,
    int ProtectedCount,
    int BlockedCount,
    long CandidateBytes,
    string GlobalBlockersJson,
    string AnomaliesJson,
    string? ErrorMessage,
    DateTime CreatedAtUtc);

public sealed record SnapshotGenerationRetentionObservation(
    long ObservationId,
    long CycleId,
    string Instrument,
    string RootSchema,
    string RootRelation,
    long SnapshotParentOid,
    long RootOid,
    string RootPartitionKey,
    string RootPartitionBound,
    string RootTablespaceName,
    string RootRelationOptionsJson,
    string RootIndexConfigurationJson,
    string ChildSchema,
    string ChildRelation,
    long SnapshotId,
    long ChildOid,
    long ChildRelfilenode,
    string PartitionBound,
    string TablespaceName,
    string RelationKind,
    string PersistenceKind,
    string AccessMethod,
    string RelationOptionsJson,
    string IndexConfigurationJson,
    string StableChildIdentityHash,
    string StableConfigSchemaHash,
    long RowEstimate,
    long TotalBytes,
    string ObservationMetricsHash,
    bool PlannerLive,
    bool OracleLive,
    string Classification,
    IReadOnlyList<string> RootReasons,
    IReadOnlyList<string> BlockerCodes,
    string DetailsJson,
    DateTime CreatedAtUtc);

public sealed record SnapshotGenerationRetentionDeferral(
    long DeferralId,
    long TriggerScrapeId,
    long TriggerPublicationId,
    string SafePointKind,
    DateTime SafePointAtUtc,
    string Code,
    string Detail,
    bool Retryable,
    string EvidenceJson,
    DateTime CreatedAtUtc);

internal sealed record SnapshotGenerationRetentionBlocker(
    string Code,
    string Detail,
    SnapshotGenerationRetentionPublicationFailureEvidence?
        PublicationFailure = null);

internal sealed record SnapshotGenerationRetentionAnomaly(
    string Code,
    string Detail,
    long? PublicationId = null,
    long? ScrapeId = null,
    string? PublicationStatus = null,
    SnapshotGenerationRetentionPublicationFailureEvidence?
        PublicationFailure = null);

internal sealed record
    SnapshotGenerationRetentionPublicationFailureEvidence(
        long PublicationId,
        long? ScrapeId,
        string PublicationStatus,
        DateTime? PublicationFailedAtUtc,
        string? PublicationFailurePhase,
        string? ScrapeStatus,
        DateTime? ScrapeCompletedAtUtc,
        DateTime? ScrapeFailedAtUtc,
        bool TerminalFailureIdentityValid,
        IReadOnlyList<string> NamedPointerSlots,
        bool ConfiguredResumeScrape,
        bool PublishedScrapeReference,
        bool PublicationFreezeReference,
        bool PublicationCommitIntentReference,
        bool MaxScoreMutationGateReference,
        bool NotificationStateReference,
        long SurfaceBindingRowCount,
        long LiveSurfaceBindingRowCount,
        long BuildingSurfaceBindingRowCount,
        long ReadySurfaceBindingRowCount,
        long FailedSurfaceBindingRowCount,
        long RetiredSurfaceBindingRowCount,
        long InvalidSurfaceBindingRowCount,
        long PublishedSourceRowCount,
        long ApiResponseCacheRowCount,
        long ApiResponseCacheStagingRowCount,
        long SongCatalogRowCount,
        long PathArtifactRowCount,
        long PreparedBandRelationCount,
        long RetainedBandRelationCount,
        long LeaderboardStagingRowCount,
        long LeaderboardStagingMetadataRowCount,
        long DeepScrapeQueueRowCount,
        long UnreplayedWriterFailureCount,
        IReadOnlyList<string> RecoveryReasons)
{
    public long LiveArtifactRowCount =>
        LiveSurfaceBindingRowCount
        + ApiResponseCacheRowCount
        + ApiResponseCacheStagingRowCount
        + SongCatalogRowCount
        + PathArtifactRowCount
        + PreparedBandRelationCount
        + RetainedBandRelationCount
        + LeaderboardStagingRowCount
        + LeaderboardStagingMetadataRowCount
        + DeepScrapeQueueRowCount;
}

public sealed record
    SnapshotGenerationRetentionPublicationSourceValidation(
        string Slot,
        long PublicationId,
        long ScrapeId,
        long? ExpectedRowCount,
        long? BindingRowCount,
        long ActualRowCount,
        string? BindingKeyHash,
        string ActualKeyHash,
        int InvalidRowCount,
        int DuplicateKeyCount,
        bool BindingIdentityValid)
{
    public bool IsValid =>
        ExpectedRowCount is > 0
        && BindingRowCount == ExpectedRowCount
        && ActualRowCount == ExpectedRowCount
        && InvalidRowCount == 0
        && DuplicateKeyCount == 0
        && BindingIdentityValid
        && PublishedScopeSourceBindingContract.IsKeyHash(
            BindingKeyHash)
        && string.Equals(
            BindingKeyHash,
            ActualKeyHash,
            StringComparison.Ordinal);

    public string ComparisonKey =>
        TierZeroCanonicalJson.SerializeToString(new
        {
            Slot,
            PublicationId,
            ScrapeId,
            ExpectedRowCount,
            BindingRowCount,
            ActualRowCount,
            BindingKeyHash,
            ActualKeyHash,
            InvalidRowCount,
            DuplicateKeyCount,
            BindingIdentityValid,
            IsValid,
        });
}

internal sealed record SnapshotGenerationRetentionChild(
    SnapshotGenerationRetentionInstrument InstrumentDefinition,
    string RootSchema,
    long SnapshotParentOid,
    long RootOid,
    string RootPartitionKey,
    string RootPartitionBound,
    string RootTablespaceName,
    IReadOnlyList<string> RootRelationOptions,
    IReadOnlyList<SnapshotGenerationRetentionIndex> RootIndexes,
    string ChildSchema,
    string ChildRelation,
    long SnapshotId,
    long ChildOid,
    long ChildRelfilenode,
    string PartitionBound,
    string TablespaceName,
    string RelationKind,
    string PersistenceKind,
    string AccessMethod,
    IReadOnlyList<string> RelationOptions,
    IReadOnlyList<SnapshotGenerationRetentionIndex> Indexes,
    long RowEstimate,
    long TotalBytes,
    IReadOnlyList<SnapshotGenerationRetentionBlocker> TopologyBlockers)
{
    public string PhysicalKey =>
        string.Join(
            "|",
            InstrumentDefinition.Instrument,
            RootSchema,
            InstrumentDefinition.RootRelation,
            SnapshotParentOid,
            RootOid,
            RootPartitionKey,
            RootPartitionBound,
            ChildSchema,
            ChildRelation,
            SnapshotId,
            ChildOid,
            ChildRelfilenode,
            PartitionBound);

    public string StableChildIdentityHash =>
        TierZeroCanonicalJson.Sha256Hex(
            TierZeroCanonicalJson.Serialize(new
            {
                InstrumentDefinition.Instrument,
                RootSchema,
                InstrumentDefinition.RootRelation,
                SnapshotParentOid,
                RootOid,
                RootPartitionKey,
                RootPartitionBound,
                ChildSchema,
                ChildRelation,
                SnapshotId,
                ChildOid,
                ChildRelfilenode,
                PartitionBound,
            }));

    public string StableConfigSchemaHash =>
        TierZeroCanonicalJson.Sha256Hex(
            TierZeroCanonicalJson.Serialize(new
            {
                InstrumentDefinition.Instrument,
                RootSchema,
                InstrumentDefinition.RootRelation,
                SnapshotParentOid,
                RootOid,
                RootPartitionKey,
                RootPartitionBound,
                ChildSchema,
                ChildRelation,
                SnapshotId,
                ChildOid,
                ChildRelfilenode,
                PartitionBound,
                RootTablespaceName,
                RootRelationOptions = RootRelationOptions
                    .OrderBy(
                        static option => option,
                        StringComparer.Ordinal),
                RootIndexes = RootIndexes
                    .OrderBy(
                        static index => index.IndexName,
                        StringComparer.Ordinal)
                    .ThenBy(static index => index.IndexOid),
                TablespaceName,
                RelationKind,
                PersistenceKind,
                AccessMethod,
                RelationOptions = RelationOptions
                    .OrderBy(
                        static option => option,
                        StringComparer.Ordinal),
                Indexes = Indexes
                    .OrderBy(
                        static index => index.IndexName,
                        StringComparer.Ordinal)
                    .ThenBy(static index => index.IndexOid),
            }));

    public string ObservationMetricsHash =>
        TierZeroCanonicalJson.Sha256Hex(
            TierZeroCanonicalJson.Serialize(new
            {
                StableChildIdentityHash,
                RowEstimate,
                TotalBytes,
            }));
}

internal sealed record SnapshotGenerationRetentionIndex(
    long TableOid,
    long IndexOid,
    long IndexRelfilenode,
    string IndexName,
    string RelationKind,
    bool IsValid,
    bool IsReady,
    bool IsPrimary,
    bool IsUnique,
    string AccessMethod,
    string TablespaceName,
    long? ParentIndexOid,
    string Definition);

public sealed record
    SnapshotGenerationRetentionNumericChildIndexValidation(
        string Instrument,
        long SnapshotId,
        string ChildRelation,
        IReadOnlyList<string> IndexKeys,
        int ExpectedParentIndexCount,
        int MissingParentIndexCount,
        int DuplicateParentIndexCount,
        int DetachedIndexCount,
        int InvalidIndexCount,
        int UnreadyIndexCount,
        int AttributeMismatchIndexCount)
{
    public bool IsValid =>
        MissingParentIndexCount == 0
        && DuplicateParentIndexCount == 0
        && DetachedIndexCount == 0
        && InvalidIndexCount == 0
        && UnreadyIndexCount == 0
        && AttributeMismatchIndexCount == 0
        && IndexKeys.Count == ExpectedParentIndexCount;

    public string ComparisonKey =>
        TierZeroCanonicalJson.SerializeToString(new
        {
            Instrument,
            SnapshotId,
            ChildRelation,
            IndexKeys,
            ExpectedParentIndexCount,
            MissingParentIndexCount,
            DuplicateParentIndexCount,
            DetachedIndexCount,
            InvalidIndexCount,
            UnreadyIndexCount,
            AttributeMismatchIndexCount,
            IsValid,
        });
}

public sealed record
    SnapshotGenerationRetentionIndexTopologyValidation(
        string Instrument,
        IReadOnlyList<string> TopIndexKeys,
        IReadOnlyList<string> RootIndexKeys,
        IReadOnlyList<string> DefaultIndexKeys,
        IReadOnlyList<string> MissingRequiredTopIndexNames,
        int InvalidTopIndexCount,
        int UnreadyTopIndexCount,
        int AttachedTopIndexCount,
        int MissingRootIndexCount,
        int DuplicateRootIndexCount,
        int DetachedRootIndexCount,
        int InvalidRootIndexCount,
        int UnreadyRootIndexCount,
        int MissingDefaultIndexCount,
        int DuplicateDefaultIndexCount,
        int DetachedDefaultIndexCount,
        int InvalidDefaultIndexCount,
        int UnreadyDefaultIndexCount,
        IReadOnlyList<
            SnapshotGenerationRetentionNumericChildIndexValidation>?
            NumericChildIndexValidations = null)
{
    public IReadOnlyList<
        SnapshotGenerationRetentionNumericChildIndexValidation>
        EffectiveNumericChildIndexValidations =>
        NumericChildIndexValidations ?? [];

    public bool IsValid =>
        MissingRequiredTopIndexNames.Count == 0
        && InvalidTopIndexCount == 0
        && UnreadyTopIndexCount == 0
        && AttachedTopIndexCount == 0
        && MissingRootIndexCount == 0
        && DuplicateRootIndexCount == 0
        && DetachedRootIndexCount == 0
        && InvalidRootIndexCount == 0
        && UnreadyRootIndexCount == 0
        && MissingDefaultIndexCount == 0
        && DuplicateDefaultIndexCount == 0
        && DetachedDefaultIndexCount == 0
        && InvalidDefaultIndexCount == 0
        && UnreadyDefaultIndexCount == 0
        && EffectiveNumericChildIndexValidations.All(
            static validation => validation.IsValid);

    public string ComparisonKey =>
        TierZeroCanonicalJson.SerializeToString(new
        {
            Instrument,
            TopIndexKeys,
            RootIndexKeys,
            DefaultIndexKeys,
            MissingRequiredTopIndexNames,
            InvalidTopIndexCount,
            UnreadyTopIndexCount,
            AttachedTopIndexCount,
            MissingRootIndexCount,
            DuplicateRootIndexCount,
            DetachedRootIndexCount,
            InvalidRootIndexCount,
            UnreadyRootIndexCount,
            MissingDefaultIndexCount,
            DuplicateDefaultIndexCount,
            DetachedDefaultIndexCount,
            InvalidDefaultIndexCount,
            UnreadyDefaultIndexCount,
            NumericChildIndexValidations =
                EffectiveNumericChildIndexValidations
                    .Select(static validation =>
                        validation.ComparisonKey)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
            IsValid,
        });

    internal static string IndexKey(
        SnapshotGenerationRetentionIndex index) =>
        string.Join(
            "|",
            index.TableOid,
            index.IndexOid,
            index.IndexRelfilenode,
            index.IndexName,
            index.RelationKind,
            index.IsValid,
            index.IsReady,
            index.IsPrimary,
            index.IsUnique,
            index.AccessMethod,
            index.TablespaceName,
            index.ParentIndexOid?.ToString() ?? "null",
            index.Definition);
}

public sealed record SnapshotGenerationRetentionOracleResult(
    IReadOnlySet<string> ChildKeys,
    IReadOnlySet<string> LiveKeys,
    IReadOnlyList<
        SnapshotGenerationRetentionPublicationSourceValidation>?
        PublicationSourceValidations = null,
    IReadOnlyList<
        SnapshotGenerationRetentionIndexTopologyValidation>?
        IndexTopologyValidations = null)
{
    public IReadOnlySet<string> CandidateKeys =>
        ChildKeys
            .Where(key => !LiveKeys.Contains(key))
            .ToHashSet(StringComparer.Ordinal);

    internal IReadOnlyList<
        SnapshotGenerationRetentionPublicationSourceValidation>
        EffectivePublicationSourceValidations =>
        PublicationSourceValidations ?? [];

    internal IReadOnlyList<
        SnapshotGenerationRetentionIndexTopologyValidation>
        EffectiveIndexTopologyValidations =>
        IndexTopologyValidations ?? [];
}

internal sealed record SnapshotGenerationRetentionSetComparison(
    bool Agrees,
    bool PublicationSourceValidationAgrees,
    bool IndexTopologyValidationAgrees,
    IReadOnlyList<string> PlannerOnlyChildren,
    IReadOnlyList<string> OracleOnlyChildren,
    IReadOnlyList<string> PlannerOnlyLive,
    IReadOnlyList<string> OracleOnlyLive,
    IReadOnlyList<string> PlannerOnlyCandidates,
    IReadOnlyList<string> OracleOnlyCandidates);

internal sealed record SnapshotGenerationRetentionEvaluation(
    SnapshotGenerationRetentionChild Child,
    bool PlannerLive,
    bool OracleLive,
    IReadOnlyList<string> RootReasons,
    IReadOnlyList<SnapshotGenerationRetentionBlocker> Blockers,
    string Classification);

internal sealed record SnapshotGenerationRetentionPersistRequest(
    SnapshotGenerationRetentionPlanRequest Request,
    string Status,
    bool OracleAgreement,
    string CandidateIdentityHash,
    string ObservationHash,
    IReadOnlyList<string> PlannerChildKeys,
    IReadOnlyList<string> PlannerLiveKeys,
    IReadOnlyList<string> PlannerCandidateKeys,
    IReadOnlyList<string> OracleChildKeys,
    IReadOnlyList<string> OracleLiveKeys,
    IReadOnlyList<string> OracleCandidateKeys,
    IReadOnlyList<
        SnapshotGenerationRetentionPublicationSourceValidation>
        PlannerPublicationSourceValidations,
    IReadOnlyList<
        SnapshotGenerationRetentionPublicationSourceValidation>
        OraclePublicationSourceValidations,
    IReadOnlyList<
        SnapshotGenerationRetentionIndexTopologyValidation>
        PlannerIndexTopologyValidations,
    IReadOnlyList<
        SnapshotGenerationRetentionIndexTopologyValidation>
        OracleIndexTopologyValidations,
    IReadOnlyList<SnapshotGenerationRetentionEvaluation> Evaluations,
    IReadOnlyList<SnapshotGenerationRetentionBlocker> GlobalBlockers,
    IReadOnlyList<SnapshotGenerationRetentionAnomaly> Anomalies,
    string? ErrorMessage);
