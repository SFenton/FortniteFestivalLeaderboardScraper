using System.Text.Json;
using System.Text.Json.Serialization;

namespace FstStoredRankRollout;

public enum ScopeSourceClass
{
    Current,
    Reused,
    Empty,
    SourceMismatch,
    ProjectionMissing,
}

public sealed class RolloutManifest
{
    public int SchemaVersion { get; init; } = 4;
    public int Seed { get; init; }
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public long PublishedScrapeId { get; init; }
    public bool PublicReadsFrozen { get; init; }
    public string ServiceImageReference { get; init; } = "";
    public string ServiceImageId { get; init; } = "";
    public string WorkerContainerId { get; init; } = "";
    public string WorkerImageReference { get; init; } = "";
    public string WorkerImageId { get; init; } = "";
    public string WorkerContainerStatus { get; init; } = "";
    public string WorkerContainerState { get; init; } = "";
    public DatabaseIdentityEvidence DatabaseIdentity { get; init; } = new();
    public ServiceDatabaseTarget ServiceDatabaseTarget { get; init; } = new();
    public string PostgresContainerId { get; init; } = "";
    public string PostgresImageReference { get; init; } = "";
    public string PostgresImageId { get; init; } = "";
    public IReadOnlyList<string> PostgresNetworkNames { get; init; } = [];
    public IReadOnlyList<string> PostgresNetworkAliases { get; init; } = [];
    public IReadOnlyList<string> PostgresServerAddresses { get; init; } = [];
    public IReadOnlyList<PostgresNetworkBinding> PostgresNetworkBindings { get; init; } = [];
    public string EvidenceMountTarget { get; init; } = "";
    public string EvidenceMountSource { get; init; } = "";
    public string EvidenceMountFileSystem { get; init; } = "";
    public string SelectionFingerprint { get; set; } = "";
    public string SelectionGuardFingerprint { get; init; } = "";
    public IReadOnlyList<string> RequiredInstruments { get; init; } = [];
    public IReadOnlyList<ScopeEvidence> Scopes { get; init; } = [];
    public IReadOnlyList<RowParityCase> RowCases { get; init; } = [];
    public IReadOnlyList<ApiWorkload> ApiWorkloads { get; init; } = [];
    public CoverageSummary Coverage { get; init; } = new();
}

public sealed class DatabaseIdentityEvidence
{
    public string DatabaseName { get; init; } = "";
    public string SystemIdentifier { get; init; } = "";
    public string ServerAddress { get; init; } = "";
    public int ServerPort { get; init; }
    public string UnixSocketDirectories { get; init; } = "";
}

public sealed class ServiceDatabaseTarget
{
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string Database { get; init; } = "";
    public string Username { get; init; } = "";
}

public sealed class PostgresNetworkBinding
{
    public string NetworkName { get; init; } = "";
    public string NetworkId { get; init; } = "";
    public string ServiceAlias { get; init; } = "";
    public string ExclusiveOwnerContainerId { get; init; } = "";
    public IReadOnlyList<string> ServerAddresses { get; init; } = [];
}

public sealed class DatabaseAttestationReport
{
    public DateTimeOffset ObservedAtUtc { get; init; }
    public DatabaseIdentityEvidence Expected { get; init; } = new();
    public DatabaseIdentityEvidence Observed { get; init; } = new();
    public ServiceDatabaseTarget ServiceDatabaseTarget { get; init; } = new();
    public string PostgresContainerId { get; init; } = "";
    public string PostgresImageReference { get; init; } = "";
    public string PostgresImageId { get; init; } = "";
    public IReadOnlyList<string> PostgresNetworkNames { get; init; } = [];
    public IReadOnlyList<string> PostgresNetworkAliases { get; init; } = [];
    public IReadOnlyList<string> PostgresServerAddresses { get; init; } = [];
    public IReadOnlyList<PostgresNetworkBinding> PostgresNetworkBindings { get; init; } = [];
    public bool Passed { get; init; }
    public IReadOnlyList<string> Failures { get; init; } = [];
}

public sealed class ScopeEvidence
{
    public string Id { get; init; } = "";
    public string SongId { get; init; } = "";
    public string Instrument { get; init; } = "";
    public long PublishedScrapeId { get; init; }
    public string SourceKind { get; init; } = "";
    public long? SourceSnapshotId { get; init; }
    public long SourceScrapeId { get; init; }
    public long ProjectionSourceSnapshotId { get; init; }
    public long PublishedRowCount { get; init; }
    public string ContentFingerprint { get; init; } = "";
    public string CoverageFingerprint { get; init; } = "";
    public long? ProjectionGeneration { get; init; }
    public long? ProjectionRowCount { get; init; }
    public long? ProjectionScopeSourceSnapshotId { get; init; }
    public string? ProjectionStatus { get; init; }
    public ScopeSourceClass SourceClass { get; init; }
    public bool HasActiveOverlay { get; init; }
    public int? RawMaxScore { get; init; }
    public IReadOnlyList<TieEvidence> ExactScoreTimeTies { get; init; } = [];
    public IReadOnlyList<SampleAccount> SampleAccounts { get; init; } = [];
    public IReadOnlyList<ExpectedLeaderboardRow> OverlayDerivedRows { get; init; } = [];
    public ThresholdBoundaryEvidence? ThresholdBoundary { get; init; }
}

public sealed class TieEvidence
{
    public int Score { get; init; }
    public string OrderTime { get; init; } = "";
    public int MinRank { get; init; }
    public long PeerCount { get; init; }
    public IReadOnlyList<string> AccountIds { get; init; } = [];
}

public sealed class SampleAccount
{
    public string AccountId { get; init; } = "";
    public int Score { get; init; }
    public int Rank { get; init; }
    public int ApiRank { get; init; }
    public string? EndTime { get; init; }
    public string Source { get; init; } = "";
    public string EvidenceKind { get; init; } = "";
}

public sealed class RowParityCase
{
    public string Id { get; init; } = "";
    public string ScopeId { get; init; } = "";
    public string SongId { get; init; } = "";
    public string Instrument { get; init; } = "";
    public int MaxScore { get; init; }
    public int? RawMaxScore { get; init; }
    public int? LeewayTenths { get; init; }
    public int? Top { get; init; }
    public int Offset { get; init; }
    public int? ExpectedFirstRank { get; init; }
    public int MinimumExpectedRows { get; init; }
    public int? ExpectedTotalCount { get; init; }
    public IReadOnlyList<ExpectedLeaderboardRow> ExpectedRows { get; init; } = [];
    public IReadOnlyList<string> ExpectedAbsentAccountIds { get; init; } = [];
    public IReadOnlyList<string> AccountIds { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public bool Core { get; init; }
}

public sealed class ApiWorkload
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Path { get; init; } = "";
    public string? SongId { get; init; }
    public string? Instrument { get; init; }
    public int ExpectedStatusCode { get; init; } = 200;
    public IReadOnlyList<string> AccountIds { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public bool Core { get; init; }
    public bool Benchmark { get; init; }
}

public sealed class CoverageSummary
{
    public IReadOnlyList<string> CoveredInstruments { get; init; } = [];
    public IReadOnlyList<string> MissingInstruments { get; init; } = [];
    public IReadOnlyList<string> CoveredSourceClasses { get; init; } = [];
    public IReadOnlyList<string> MissingSourceClasses { get; init; } = [];
    public IReadOnlyList<string> CoveredApiKinds { get; init; } = [];
    public IReadOnlyList<string> MissingApiKinds { get; init; } = [];
    public bool HasExactScoreTimeTie { get; init; }
    public bool HasActiveOverlay { get; init; }
    public bool HasSourceMatchedOverlayRow { get; init; }
    public bool HasRankPageBoundary99 { get; init; }
    public bool HasRankPageBoundary100 { get; init; }
    public bool HasRankPageBoundary { get; init; }
    public bool HasThresholdEdges { get; init; }
    public bool HasFractionalThresholdTruncation { get; init; }
    public bool PromotionReady { get; init; }
    public IReadOnlyList<string> MissingRequirements { get; init; } = [];
}

public sealed class ExpectedLeaderboardRow
{
    public string AccountId { get; init; } = "";
    public int Score { get; init; }
    public int Rank { get; init; }
    public string Source { get; init; } = "";
}

public sealed class ThresholdBoundaryEvidence
{
    public int RawMaxScore { get; init; }
    public int LeewayTenths { get; init; }
    public int Threshold { get; init; }
    public int BelowTotalCount { get; init; }
    public int ExactTotalCount { get; init; }
    public int PlusTotalCount { get; init; }
    public IReadOnlyList<ExpectedLeaderboardRow> ExactAddedRows { get; init; } = [];
    public IReadOnlyList<ExpectedLeaderboardRow> PlusAddedRows { get; init; } = [];
}

public sealed class ParityReport
{
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset CompletedAtUtc { get; set; }
    public long PublishedScrapeId { get; init; }
    public string ManifestFingerprint { get; init; } = "";
    public string InitialGuardFingerprint { get; init; } = "";
    public string EndingGuardFingerprint { get; set; } = "";
    public int CaseCount { get; set; }
    public int DifferenceCount { get; set; }
    public bool PageBoundariesPassed { get; set; }
    public bool Passed { get; set; }
    public IReadOnlyList<ParityCaseResult> Cases { get; set; } = [];
    public IReadOnlyList<PageBoundaryExecutionEvidence> PageBoundaries { get; set; } = [];
}

public sealed class ManifestGuardReport
{
    public DateTimeOffset ObservedAtUtc { get; init; }
    public long ExpectedPublishedScrapeId { get; init; }
    public long PublishedScrapeId { get; init; }
    public bool PublicReadsFrozen { get; init; }
    public string ExpectedGuardFingerprint { get; init; } = "";
    public string ObservedGuardFingerprint { get; init; } = "";
    public DatabaseAttestationReport DatabaseAttestation { get; init; } = new();
    public bool Passed { get; init; }
    public IReadOnlyList<string> Failures { get; init; } = [];
}

public sealed class RolloutPreflightReport
{
    public DateTimeOffset ObservedAtUtc { get; init; }
    public long ExpectedPublishedScrapeId { get; init; }
    public long PublishedScrapeId { get; init; }
    public bool PublicReadsFrozen { get; init; }
    public string ScrapeStatus { get; init; } = "";
    public DateTimeOffset? ScrapeCompletedAtUtc { get; init; }
    public long CompletePublishedScopeCount { get; init; }
    public long IncompletePublishedScopeCount { get; init; }
    public long ActiveScrapeId { get; init; }
    public bool WorkerHasActiveOperation { get; init; }
    public string WorkerStatus { get; init; } = "";
    public DateTimeOffset? WorkerLastHeartbeatAtUtc { get; init; }
    public long ActiveWorkerConnectionCount { get; init; }
    public IReadOnlyList<string> ActiveWorkerApplications { get; init; } = [];
    public long GrantedMutationLeaseCount { get; init; }
    public long ActiveDurableJobCount { get; init; }
    public bool HasPgMonitor { get; init; }
    public bool HasPgReadAllStats { get; init; }
    public bool MonitoringPrivilegeAttested { get; init; }
    public bool CrossRoleVisibilityAttested { get; set; }
    public long UngrantedLockCount { get; init; }
    public long LongRunningQueryCount { get; init; }
    public DatabaseAttestationReport? DatabaseAttestation { get; init; }
    public bool Passed { get; init; }
    public IReadOnlyList<string> Failures { get; init; } = [];
}

public sealed class ParityCaseResult
{
    public string CaseId { get; init; } = "";
    public string SongId { get; init; } = "";
    public string Instrument { get; init; } = "";
    public int BaselineTotalCount { get; init; }
    public int CandidateTotalCount { get; init; }
    public int BaselineRowCount { get; init; }
    public int CandidateRowCount { get; init; }
    public int AccountCount { get; init; }
    public PageBoundaryExecutionEvidence? PageBoundary { get; init; }
    public IReadOnlyList<ParityDifference> Differences { get; init; } = [];
}

public sealed class PageBoundaryExecutionEvidence
{
    public string CaseId { get; init; } = "";
    public int Offset { get; init; }
    public int ExpectedFirstRank { get; init; }
    public int MinimumExpectedRows { get; init; }
    public int BaselineTotalCount { get; init; }
    public int CandidateTotalCount { get; init; }
    public int BaselineRowCount { get; init; }
    public int CandidateRowCount { get; init; }
    public int? BaselineFirstRank { get; init; }
    public int? CandidateFirstRank { get; init; }
    public bool Passed { get; init; }
}

public sealed class ParityDifference
{
    public string Surface { get; init; } = "";
    public string Key { get; init; } = "";
    public string Field { get; init; } = "";
    public string? Baseline { get; init; }
    public string? Candidate { get; init; }
}

public sealed class ComparableLeaderboardRow
{
    public string AccountId { get; init; } = "";
    public int Score { get; init; }
    public int Rank { get; init; }
    public int Accuracy { get; init; }
    public bool IsFullCombo { get; init; }
    public int Stars { get; init; }
    public int Season { get; init; }
    public int Difficulty { get; init; }
    public double Percentile { get; init; }
    public string? EndTime { get; init; }
    public int ApiRank { get; init; }
    public string Source { get; init; } = "";
}

public sealed class ApiCaptureReport
{
    public string Variant { get; init; } = "";
    public DateTimeOffset CapturedAtUtc { get; init; }
    public string ManifestFingerprint { get; init; } = "";
    public int UnexpectedStatusCount { get; init; }
    public bool Passed { get; init; }
    public IReadOnlyList<ApiCaptureItem> Items { get; init; } = [];
}

public sealed class ApiCaptureItem
{
    public string WorkloadId { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Path { get; init; } = "";
    public int ExpectedStatusCode { get; init; } = 200;
    public int StatusCode { get; init; }
    public string ContentType { get; init; } = "";
    public string? ETag { get; init; }
    public string BodySha256 { get; init; } = "";
    public long BodyLength { get; init; }
    public string BodyFile { get; init; } = "";
}

public sealed class ApiComparisonReport
{
    public DateTimeOffset ComparedAtUtc { get; init; }
    public string BaselineVariant { get; init; } = "";
    public string CandidateVariant { get; init; } = "";
    public int WorkloadCount { get; init; }
    public int DifferenceCount { get; init; }
    public bool Passed { get; init; }
    public IReadOnlyList<ParityDifference> Differences { get; init; } = [];
}

public sealed class BenchmarkScheduleEntry
{
    public int Sequence { get; init; }
    public string Mode { get; init; } = "";
    public int Concurrency { get; init; }
    public string WorkloadId { get; init; } = "";
    public int AbbaBlock { get; init; }
    public int Position { get; init; }
    public string Variant { get; init; } = "";
    public int RequestCount { get; init; }
}

public sealed class BenchmarkBlockReport
{
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset CompletedAtUtc { get; set; }
    public int Sequence { get; init; }
    public string Variant { get; init; } = "";
    public string Mode { get; init; } = "";
    public int Concurrency { get; init; }
    public string WorkloadId { get; init; } = "";
    public string Path { get; init; } = "";
    public int RequestedCount { get; init; }
    public int CompletedCount { get; set; }
    public int ErrorCount { get; set; }
    public int WarmRequestStartsPerSecond { get; init; }
    public DateTimeOffset SamplerArmedAtUtc { get; init; }
    public DateTimeOffset RequestsStartedAtUtc { get; init; }
    public DateTimeOffset HttpRequestsCompletedAtUtc { get; init; }
    public DateTimeOffset RequestsCompletedAtUtc { get; init; }
    public IReadOnlyList<double> LatencyMilliseconds { get; set; } = [];
    public IReadOnlyDictionary<int, int> StatusCounts { get; set; } = new Dictionary<int, int>();
    public IReadOnlyList<string> BodyFingerprints { get; set; } = [];
    public DatabaseAttestationReport? DatabaseAttestation { get; set; }
    public DatabaseResourceSnapshot DatabaseStart { get; set; } = new();
    public DatabaseResourceSnapshot DatabaseEnd { get; set; } = new();
    public IReadOnlyList<ContainerResourceSample> PostgresContainerSamples { get; set; } = [];
}

public sealed class DatabaseResourceSnapshot
{
    public long BlocksRead { get; init; }
    public long TempBytes { get; init; }
    public long TempFiles { get; init; }
    public DateTimeOffset? StatsResetAtUtc { get; init; }
}

public sealed class ContainerResourceSample
{
    public DateTimeOffset IntervalStartedAtUtc { get; init; }
    public DateTimeOffset IntervalCompletedAtUtc { get; init; }
    public DateTimeOffset ObservedAtUtc { get; init; }
    public double CpuPercent { get; init; }
    public long MemoryCurrentBytes { get; init; }
}

public sealed class BenchmarkAnalysisReport
{
    public DateTimeOffset AnalyzedAtUtc { get; init; }
    public bool CorrectnessPassed { get; init; }
    public bool SampleCountsPassed { get; init; }
    public bool PerformancePassed { get; init; }
    public bool ResourcesPassed { get; init; }
    public bool Passed { get; init; }
    public IReadOnlyList<BenchmarkWorkloadAnalysis> Workloads { get; init; } = [];
    public IReadOnlyList<ResourceAnalysis> Resources { get; init; } = [];
    public IReadOnlyList<string> Failures { get; init; } = [];
}

public sealed class RolloutAcceptanceReport
{
    public DateTimeOffset FinalizedAtUtc { get; init; }
    public string ManifestFingerprint { get; init; } = "";
    public BenchmarkAnalysisReport Analysis { get; init; } = new();
    public RollbackVerificationEvidence Rollback { get; init; } = new();
    public RollbackVerificationEvidence Recovery { get; init; } = new();
    public RollbackVerificationEvidence FinalRuntime { get; init; } = new();
    public RolloutPreflightReport FinalQuiescence { get; init; } = new();
    public string FinalQuiescenceSha256 { get; init; } = "";
    public bool Passed { get; init; }
    public IReadOnlyList<string> Failures { get; init; } = [];
}

public sealed class RollbackVerificationEvidence
{
    public string Label { get; init; } = "";
    public string ManifestFingerprint { get; init; } = "";
    public string FstserviceContainerId { get; init; } = "";
    public string FstserviceContainerHostname { get; init; } = "";
    public string FstserviceInstanceNonce { get; init; } = "";
    public string FstserviceBaseUrl { get; init; } = "";
    public string FstworkerContainerId { get; init; } = "";
    public string FstserviceImageReference { get; init; } = "";
    public string FstserviceImageId { get; init; } = "";
    public string FstworkerImageReference { get; init; } = "";
    public string FstworkerImageId { get; init; } = "";
    public string FstworkerContainerStatus { get; init; } = "";
    public string FstworkerContainerState { get; init; } = "";
    public bool FstserviceStoredRankFlag { get; init; }
    public bool FstworkerStoredRankFlag { get; init; }
    public bool FstservicePublishedSources { get; init; }
    public bool FstworkerPublishedSources { get; init; }
    public bool FstserviceReadOnlyStartup { get; init; }
    public bool FstworkerReadOnlyStartup { get; init; }
    public bool FstservicePostgresReadOnly { get; init; }
    public bool FstworkerPostgresReadOnly { get; init; }
    public ServiceDatabaseTarget FstserviceDatabaseTarget { get; init; } = new();
    public bool FstserviceDefaultTransactionReadOnlyOption { get; init; }
    public string PostgresContainerId { get; init; } = "";
    public string PostgresImageReference { get; init; } = "";
    public string PostgresImageId { get; init; } = "";
    public IReadOnlyList<string> PostgresNetworkNames { get; init; } = [];
    public IReadOnlyList<string> PostgresNetworkAliases { get; init; } = [];
    public IReadOnlyList<string> PostgresServerAddresses { get; init; } = [];
    public IReadOnlyList<PostgresNetworkBinding> PostgresNetworkBindings { get; init; } = [];
    public bool HealthVerified { get; init; }
}

public sealed class BenchmarkWorkloadAnalysis
{
    public string WorkloadId { get; init; } = "";
    public string Mode { get; init; } = "";
    public int Concurrency { get; init; }
    public bool Core { get; init; }
    public int BaselineSamples { get; init; }
    public int CandidateSamples { get; init; }
    public double BaselineP95Milliseconds { get; init; }
    public double CandidateP95Milliseconds { get; init; }
    public double? ChangePercent { get; init; }
    public bool Passed { get; init; }
}

public sealed class ResourceAnalysis
{
    public string Mode { get; init; } = "";
    public bool Passed { get; init; }
    public int BlockCount { get; init; }
    public int BlocksWithOverlappingSamples { get; init; }
    public double BaselineCpuP95 { get; init; }
    public double CandidateCpuP95 { get; init; }
    public bool CpuBaselineZero { get; init; }
    public double? CpuChangePercent { get; init; }
    public double BaselineMemoryP95Bytes { get; init; }
    public double CandidateMemoryP95Bytes { get; init; }
    public bool MemoryBaselineZero { get; init; }
    public double? MemoryChangePercent { get; init; }
    public double BaselineBlocksReadPerRequest { get; init; }
    public double CandidateBlocksReadPerRequest { get; init; }
    public bool BlocksReadBaselineZero { get; init; }
    public double? BlocksReadChangePercent { get; init; }
    public double BaselineTempBytesPerRequest { get; init; }
    public double CandidateTempBytesPerRequest { get; init; }
    public bool TempBytesBaselineZero { get; init; }
    public double? TempBytesChangePercent { get; init; }
    public double BaselineTempFilesPerRequest { get; init; }
    public double CandidateTempFilesPerRequest { get; init; }
    public bool TempFilesBaselineZero { get; init; }
    public double? TempFilesChangePercent { get; init; }
}

public static class RolloutJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
