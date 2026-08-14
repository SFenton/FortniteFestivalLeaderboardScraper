using System.Text;

namespace FSTService.Scraping.Replay;

public static class TierOneReplayFormat
{
    public const string InputFormatId = "fst.tier1.phase-input";
    public const string OutputFormatId = "fst.tier1.phase-output";
    public const int Version = 1;
    public const string InputManifestPath = "tier1/phase-input.json";
    public const string OutputManifestPath = "replay/output-manifest.json";
    public const string ScopesDatasetId = "band-current.requested-scopes";
    public const string EntriesDatasetId = "band-current.band-entries";
    public const string MemberStatsDatasetId = "band-current.member-stats";
    public const string ScopesPath = "datasets/requested-scopes.jsonl";
    public const string EntriesPath = "datasets/band-entries.jsonl";
    public const string MemberStatsPath = "datasets/band-member-stats.jsonl";
    public const string ProjectionOutputId = "band-current.projection";
    public const string ScopeOutputId = "band-current.scope-state";
    public const string StateOutputId = "band-current.global-state";
    public const string ProjectionOutputPath =
        "outputs/current-band-projection.jsonl";
    public const string ScopeOutputPath =
        "outputs/band-current-scope.jsonl";
    public const string StateOutputPath =
        "outputs/band-current-state.jsonl";
    public const string MetricsPath = "replay/resource-metrics.json";
    public const string FailurePath = "replay/failure.json";
}

public sealed record TierOneDatasetReference(
    string DatasetId,
    string Path,
    int SchemaVersion,
    long RowCount,
    long UncompressedBytes,
    string Sha256,
    string Completeness);

public sealed record TierOneReplayBounds(
    long MaximumPackageBytes,
    int MaximumScopes,
    int MaximumBandEntries,
    int MaximumMemberStats,
    int MaximumOutputRows,
    int StatementTimeoutSeconds,
    int LockTimeoutSeconds)
{
    public static TierOneReplayBounds Conservative { get; } =
        new(
            64L * 1024 * 1024,
            16,
            50_000,
            200_000,
            100_000,
            30,
            5);
}

public sealed record TierOnePhaseInputManifest(
    string FormatId,
    int Version,
    string ReplayId,
    string TierZeroParentRootHash,
    string PhasePlanId,
    string PhasePlanVersion,
    string PhaseId,
    string SubphaseId,
    int AdapterVersion,
    DateTimeOffset SourceCutUtc,
    string SourceDatabaseSystemIdentifier,
    IReadOnlyList<string> DependencyPhaseIds,
    IReadOnlyList<TierOneDatasetReference> Datasets,
    TierOneReplayBounds Bounds,
    string? ManifestRootHash);

public sealed record ReplayRequestedScopeRow(
    string SongId,
    string BandType,
    string RankingScope,
    string ScopeComboId);

public sealed record ReplayBandEntryRow(
    string SongId,
    string BandType,
    string TeamKey,
    string InstrumentCombo,
    IReadOnlyList<string> TeamMembers,
    int Score,
    int? Accuracy,
    bool? IsFullCombo,
    int? Stars,
    int? Difficulty,
    int? Season,
    string? EndTime,
    bool IsOverThreshold,
    DateTimeOffset FirstSeenAtUtc,
    DateTimeOffset LastUpdatedAtUtc);

public sealed record ReplayBandMemberStatRow(
    string SongId,
    string BandType,
    string TeamKey,
    string InstrumentCombo,
    int MemberIndex,
    string AccountId,
    int? InstrumentId,
    int? Score,
    int? Accuracy,
    bool? IsFullCombo,
    int? Stars,
    int? Difficulty);

public sealed record TierOneReplayInput(
    TierZeroEvidenceManifest ParentManifest,
    TierZeroEvidenceManifest InputPackageManifest,
    TierOnePhaseInputManifest InputManifest,
    IReadOnlyList<ReplayRequestedScopeRow> Scopes,
    IReadOnlyList<ReplayBandEntryRow> BandEntries,
    IReadOnlyList<ReplayBandMemberStatRow> MemberStats,
    long PackageBytes);

public sealed record ReplayPhaseDescriptor(
    string PhaseId,
    string SubphaseId,
    int AdapterVersion,
    IReadOnlyList<string> DependencyPhaseIds,
    IReadOnlyList<string> InputDatasetIds,
    IReadOnlyList<string> OutputDatasetIds,
    bool PublicationCriticalInProduction,
    string ResourceClass,
    bool SupportsProviderNetwork,
    bool SupportsPublication);

public static class ReplayPhaseCatalog
{
    public const string BandMaintenancePhaseId = "post.band_maintenance";
    public const string CurrentProjectionSubphaseId =
        "current_projection_refresh";
    public const int CurrentProjectionAdapterVersion = 1;

    public static ReplayPhaseDescriptor BandCurrentProjectionRefresh { get; } =
        new(
            BandMaintenancePhaseId,
            CurrentProjectionSubphaseId,
            CurrentProjectionAdapterVersion,
            ["post.band_extraction"],
            [
                TierOneReplayFormat.ScopesDatasetId,
                TierOneReplayFormat.EntriesDatasetId,
                TierOneReplayFormat.MemberStatsDatasetId,
            ],
            [
                TierOneReplayFormat.ProjectionOutputId,
                TierOneReplayFormat.ScopeOutputId,
                TierOneReplayFormat.StateOutputId,
            ],
            PublicationCriticalInProduction: true,
            ResourceClass: "bounded-postgres-write",
            SupportsProviderNetwork: false,
            SupportsPublication: false);

    public static ReplayPhaseDescriptor Resolve(
        string phaseId,
        string subphaseId)
    {
        if (PhaseProgressCatalog.FindById(phaseId) is null)
        {
            throw new ReplayException(
                ReplayFailureKind.Usage,
                ReplayExitCode.Usage,
                $"Unknown stable replay phase ID '{phaseId}'.");
        }
        if (!string.Equals(
                phaseId,
                BandCurrentProjectionRefresh.PhaseId,
                StringComparison.Ordinal) ||
            !string.Equals(
                subphaseId,
                BandCurrentProjectionRefresh.SubphaseId,
                StringComparison.Ordinal))
        {
            throw new ReplayException(
                ReplayFailureKind.Usage,
                ReplayExitCode.Usage,
                $"Phase '{phaseId}' subphase '{subphaseId}' is not replayable in protocol v1.");
        }
        return BandCurrentProjectionRefresh;
    }
}

public sealed record ReplayDatabaseIdentity(
    string DatabaseName,
    string SystemIdentifier,
    int PostgreSqlMajorVersion,
    IReadOnlyList<string> Extensions,
    string SchemaFingerprint);

public sealed record ReplayPhaseMetrics(
    double ElapsedMilliseconds,
    double CpuMilliseconds,
    long AllocatedBytes,
    long PeakWorkingSetBytes,
    long WalBytes,
    long TempBytes,
    int RefreshedScopes,
    int FailedScopes,
    long InsertedRows,
    long DeletedRows);

public sealed record TierOneOutputDatasetReference(
    string DatasetId,
    string Path,
    int SchemaVersion,
    long RowCount,
    string Sha256);

public sealed record TierOnePhaseOutputManifest(
    string FormatId,
    int Version,
    string ReplayId,
    int Attempt,
    string PhaseId,
    string SubphaseId,
    int AdapterVersion,
    string TierZeroParentRootHash,
    string TierOneInputRootHash,
    string PhasePlanId,
    string PhasePlanVersion,
    TierZeroBuildIdentity Implementation,
    ReplayDatabaseIdentity Database,
    IReadOnlyList<TierOneOutputDatasetReference> Outputs,
    ReplayPhaseMetrics Metrics,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    bool NoPublication,
    string? ManifestRootHash);

public sealed record ReplayOutputProjectionRow(
    string SongId,
    string BandType,
    string RankingScope,
    string ScopeComboId,
    string TeamKey,
    string EntryComboId,
    string EntryInstrumentCombo,
    IReadOnlyList<string> TeamMembers,
    IReadOnlyList<string> MemberAccountIds,
    IReadOnlyList<int> MemberInstrumentIds,
    IReadOnlyList<int> MemberScores,
    IReadOnlyList<int> MemberAccuracies,
    IReadOnlyList<int> MemberFullCombos,
    IReadOnlyList<int> MemberStars,
    IReadOnlyList<int> MemberDifficulties,
    int Score,
    int? Accuracy,
    bool? IsFullCombo,
    int? Stars,
    int? Difficulty,
    int? Season,
    int Rank,
    int TotalEntries,
    double Percentile,
    string? EndTime,
    DateTimeOffset FirstSeenAtUtc,
    DateTimeOffset LastUpdatedAtUtc,
    long ProjectionGeneration);

public sealed record ReplayOutputScopeRow(
    string SongId,
    string BandType,
    string RankingScope,
    string ScopeComboId,
    long ProjectionGeneration,
    long? PublishedGeneration,
    long RowCount,
    long PublishedRowCount,
    string Status);

public sealed record ReplayOutputStateRow(
    long CurrentGeneration,
    long RowCount,
    long ScopeCount,
    long FailedScopeCount);

public sealed record ReplayFailureRecord(
    string ReplayId,
    int Attempt,
    string PhaseId,
    string SubphaseId,
    ReplayFailureKind Kind,
    ReplayExitCode ExitCode,
    bool Cancelled,
    DateTimeOffset FailedAtUtc);

public sealed record ReplayDatasetComparison(
    string DatasetId,
    long BaselineRows,
    long CandidateRows,
    string BaselineSha256,
    string CandidateSha256,
    bool ExactParity);

public sealed record ReplayComparisonReport(
    string FormatId,
    int Version,
    string BaselinePackageRootHash,
    string CandidatePackageRootHash,
    string TierOneInputRootHash,
    string PhaseId,
    string SubphaseId,
    IReadOnlyList<ReplayDatasetComparison> Datasets,
    bool ExactParity,
    double BaselineElapsedMilliseconds,
    double CandidateElapsedMilliseconds,
    double ElapsedDeltaMilliseconds,
    double ElapsedDeltaPercent,
    long BaselineWalBytes,
    long CandidateWalBytes,
    long WalDeltaBytes,
    long BaselinePeakWorkingSetBytes,
    long CandidatePeakWorkingSetBytes,
    long PeakWorkingSetDeltaBytes);

public sealed record ReplayComparisonExpectations(
    string BaselineImageDigest,
    string BaselineGitCommit,
    string BaselineRevision,
    int BaselineAttempt,
    string CandidateImageDigest,
    string CandidateGitCommit,
    string CandidateRevision,
    int CandidateAttempt);

internal static class TierOneReplayCanonical
{
    internal static byte[] SerializeInput(
        TierOnePhaseInputManifest manifest)
    {
        var root = TierZeroCanonicalJson.Sha256Hex(
            TierZeroCanonicalJson.Serialize(
                manifest with { ManifestRootHash = null }));
        return TierZeroCanonicalJson.Serialize(
            manifest with { ManifestRootHash = root });
    }

    internal static byte[] SerializeOutput(
        TierOnePhaseOutputManifest manifest)
    {
        var root = TierZeroCanonicalJson.Sha256Hex(
            TierZeroCanonicalJson.Serialize(
                manifest with { ManifestRootHash = null }));
        return TierZeroCanonicalJson.Serialize(
            manifest with { ManifestRootHash = root });
    }

    internal static void RequireValidInputRoot(
        TierOnePhaseInputManifest manifest)
    {
        var expected = TierZeroCanonicalJson.Sha256Hex(
            TierZeroCanonicalJson.Serialize(
                manifest with { ManifestRootHash = null }));
        if (!string.Equals(
                expected,
                manifest.ManifestRootHash,
                StringComparison.Ordinal))
        {
            throw new ReplayException(
                ReplayFailureKind.PackageRejected,
                ReplayExitCode.PackageRejected,
                "Tier-1 input manifest root hash is invalid.");
        }
    }

    internal static void RequireValidOutputRoot(
        TierOnePhaseOutputManifest manifest)
    {
        var expected = TierZeroCanonicalJson.Sha256Hex(
            TierZeroCanonicalJson.Serialize(
                manifest with { ManifestRootHash = null }));
        if (!string.Equals(
                expected,
                manifest.ManifestRootHash,
                StringComparison.Ordinal))
        {
            throw new ReplayException(
                ReplayFailureKind.ComparisonFailed,
                ReplayExitCode.ComparisonFailed,
                "Tier-1 output manifest root hash is invalid.");
        }
    }

    internal static byte[] ToJsonLines<T>(
        IEnumerable<T> rows)
    {
        using var stream = new MemoryStream();
        foreach (var row in rows)
        {
            stream.Write(TierZeroCanonicalJson.Serialize(row));
            stream.WriteByte((byte)'\n');
        }
        return stream.ToArray();
    }
}
