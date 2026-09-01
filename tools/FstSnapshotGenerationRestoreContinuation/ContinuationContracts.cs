using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using FstSnapshotGenerationEvidence;
using FstSnapshotGenerationQuarantine;

namespace FstSnapshotGenerationRestoreContinuation;

public static class RestoreContinuationContract
{
    public const int SchemaVersion = 1;
    public const string ToolId =
        "fst.snapshot-generation-restore-continuation.v1";
    public const string AuthorizationToolId =
        "fst.snapshot-generation-restore-continuation-authorization.v1";
    public const string AuthorizerToolId =
        "fst.snapshot-generation-restore-tool-authorizer.v1";
    public const string PackageToolId =
        "fst.snapshot-generation-restore-continuation-package.v1";
    public const string Scope =
        "confirm_attest_finalize";

    public static string Sha256<T>(T value) =>
        Convert.ToHexString(
                SHA256.HashData(
                    SnapshotGenerationCanonicalJson.Serialize(
                        value)))
            .ToLowerInvariant();

    public static string DeriveAuthorizationId(
        RestoreContinuationAuthorizationRequest request,
        string canonicalEvidenceDbSha256) =>
        Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        string.Join(
                            ':',
                            AuthorizationToolId,
                            Scope,
                            request.RestoreOperationId,
                            request.DropOperationId,
                            request.PredecessorAuthorizationId,
                            request.RestorePlanDigest,
                            request.RestorePlanFileSha256,
                            request.RestoreReportSha256,
                            request.PredecessorRestoreToolSha256,
                            request.PredecessorRepairPackageManifestSha256,
                            request.RecoveryBundleManifestSha256,
                            request.AuthorizedContinuationToolSha256,
                            request.AuthorizedEvidenceAssemblySha256,
                            request.RouteParityReferenceSourceSha256,
                            request.AuthorizerBinarySha256,
                            request.ContinuationPackageManifestSha256,
                            request.RouteParityAlgorithmId,
                            request.RouteParityPreflightSha256,
                            request.BaselineRouteManifestSha256,
                            request.BaselineRouteChecksumsSha256,
                            request.CandidateRouteManifestSha256,
                            request.CandidateRouteChecksumsSha256,
                            request.PublicationId.ToString(
                                CultureInfo.InvariantCulture),
                            request.PublishedScrapeId.ToString(
                                CultureInfo.InvariantCulture),
                            request.RepositoryCommit,
                            request.RepositoryTreeId,
                            request.PredecessorToContinuationDiffSha256,
                            request.SourceManifestSha256,
                            request.TestEvidenceManifestSha256,
                            request.EvidenceSha256,
                            canonicalEvidenceDbSha256))))
            .ToLowerInvariant()[..32];
}

public sealed record RestoreContinuationPackageFile(
    string Path,
    string Sha256,
    long Bytes);

public sealed record RestoreContinuationPackageManifest(
    int SchemaVersion,
    string ToolId,
    string Status,
    DateTimeOffset CreatedAtUtc,
    string RestoreOperationId,
    string DropOperationId,
    string RestorePlanDigest,
    string RestorePlanPath,
    string RestorePlanFileSha256,
    string RestoreReportPath,
    string RestoreReportSha256,
    string PredecessorAuthorizationId,
    string PredecessorRestoreToolSha256,
    string PredecessorRepairPackagePath,
    string PredecessorRepairPackageManifestSha256,
    string RecoveryBundlePath,
    string RecoveryBundleManifestSha256,
    string AuthorizedContinuationToolSha256,
    string AuthorizedEvidenceAssemblySha256,
    string RouteParityReferenceSourceSha256,
    string AuthorizerBinarySha256,
    string RepositoryCommit,
    string RepositoryTreeId,
    string PredecessorToContinuationDiffSha256,
    string SourceManifestSha256,
    string TestEvidenceManifestSha256,
    string RouteParityAlgorithmId,
    string RouteParityPreflightSha256,
    string BaselineRouteManifestPath,
    string BaselineRouteManifestSha256,
    string BaselineRouteChecksumsSha256,
    string CandidateRouteManifestPath,
    string CandidateRouteManifestSha256,
    string CandidateRouteChecksumsSha256,
    long PublicationId,
    long PublishedScrapeId,
    IReadOnlyList<RestoreContinuationPackageFile> Files);

public sealed record RestoreContinuationAuthorizationRequest(
    string RestoreOperationId,
    string DropOperationId,
    string PredecessorAuthorizationId,
    string RestorePlanDigest,
    string RestorePlanFileSha256,
    string RestoreReportSha256,
    string PredecessorRestoreToolSha256,
    string PredecessorRepairPackageManifestSha256,
    string RecoveryBundleManifestSha256,
    string AuthorizedContinuationToolSha256,
    string AuthorizedEvidenceAssemblySha256,
    string RouteParityReferenceSourceSha256,
    string AuthorizerBinarySha256,
    string ContinuationPackageManifestSha256,
    string RouteParityAlgorithmId,
    string RouteParityPreflightSha256,
    string BaselineRouteManifestSha256,
    string BaselineRouteChecksumsSha256,
    string CandidateRouteManifestSha256,
    string CandidateRouteChecksumsSha256,
    long PublicationId,
    long PublishedScrapeId,
    string RepositoryCommit,
    string RepositoryTreeId,
    string PredecessorToContinuationDiffSha256,
    string SourceManifestSha256,
    string TestEvidenceManifestSha256,
    string ReasonCode,
    string ReasonText,
    string ApprovedBy,
    string ReviewedBy,
    string ApprovalReference,
    JsonElement CanonicalEvidence)
{
    [JsonIgnore]
    public string EvidenceSha256 =>
        RestoreContinuationContract.Sha256(this);
}

public sealed record RestoreContinuationAuthorizationRecord(
    string ContinuationAuthorizationId,
    string RestoreOperationId,
    string DropOperationId,
    string PredecessorAuthorizationId,
    string RestorePlanDigest,
    string RestorePlanFileSha256,
    string RestoreReportSha256,
    string PredecessorRestoreToolSha256,
    string PredecessorRepairPackageManifestSha256,
    string RecoveryBundleManifestSha256,
    string AuthorizedContinuationToolSha256,
    string AuthorizedEvidenceAssemblySha256,
    string RouteParityReferenceSourceSha256,
    string AuthorizerBinarySha256,
    string ContinuationPackageManifestSha256,
    string RouteParityAlgorithmId,
    string RouteParityPreflightSha256,
    string BaselineRouteManifestSha256,
    string BaselineRouteChecksumsSha256,
    string CandidateRouteManifestSha256,
    string CandidateRouteChecksumsSha256,
    long PublicationId,
    long PublishedScrapeId,
    string RepositoryCommit,
    string RepositoryTreeId,
    string PredecessorToContinuationDiffSha256,
    string SourceManifestSha256,
    string TestEvidenceManifestSha256,
    string ReasonCode,
    string ReasonText,
    string ApprovedBy,
    string ReviewedBy,
    string ApprovalReference,
    JsonElement CanonicalEvidence,
    string EvidenceSha256,
    string CanonicalEvidenceDbSha256,
    string DatabaseUser,
    int BackendPid,
    string TransactionId,
    DateTimeOffset AuthorizedAt);

public sealed record RestoreContinuationAuthorizationReport(
    int SchemaVersion,
    string ToolId,
    string Action,
    string Status,
    DateTimeOffset CompletedAtUtc,
    string ContinuationAuthorizationId,
    RestoreContinuationAuthorizationRequest Request,
    RestoreContinuationAuthorizationRecord DatabaseEvidence,
    string? ReportSha256 = null)
{
    public RestoreContinuationAuthorizationReport Seal() =>
        this with
        {
            ReportSha256 =
                RestoreContinuationContract.Sha256(
                    this with { ReportSha256 = null }),
        };
}

public sealed record RestoreContinuationPreflightReport(
    int SchemaVersion,
    string ToolId,
    string Status,
    DateTimeOffset CompletedAtUtc,
    string RouteParityAlgorithmId,
    string RouteParityReferenceSourceSha256,
    string EvidenceAssemblySha256,
    DetailedRouteParityEvidence PostRestore,
    DetailedRouteParityEvidence RepeatedPostRestore,
    string BandExportSemanticSha256,
    string PlayerExportSemanticSha256,
    string? ReportSha256 = null)
{
    public RestoreContinuationPreflightReport Seal() =>
        this with
        {
            ReportSha256 =
                RestoreContinuationContract.Sha256(
                    this with { ReportSha256 = null }),
        };
}

public sealed record RestoreContinuationCommandReport(
    int SchemaVersion,
    string ToolId,
    string Action,
    string Status,
    DateTimeOffset CompletedAtUtc,
    string RestoreOperationId,
    string ContinuationAuthorizationId,
    string EvidenceToolSha256,
    JsonElement DatabaseEvidence,
    DetailedRouteParityEvidence? RouteParity = null,
    string? ReportSha256 = null)
{
    public RestoreContinuationCommandReport Seal() =>
        this with
        {
            ReportSha256 =
                RestoreContinuationContract.Sha256(
                    this with { ReportSha256 = null }),
        };
}
