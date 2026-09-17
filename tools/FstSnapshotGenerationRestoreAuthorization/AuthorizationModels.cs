using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FstSnapshotGenerationDrop;

namespace FstSnapshotGenerationRestoreAuthorization;

public static class RestoreToolAuthorizationContract
{
    public const int SchemaVersion = 1;
    public const string ToolId =
        "fst.snapshot-generation-restore-tool-authorization.v1";
    public const string AuthorizerToolId =
        "fst.snapshot-generation-restore-tool-authorizer.v1";
    public const string RepairPackageToolId =
        "fst.snapshot-generation-restore-tool-repair-package.v1";
    public const string ValidatorBaseToolSha256 =
        "acb358604d9f642da3d4809581328f76118cb912c32765353b8594cc68a1522d";

    public static string DeriveAuthorizationId(
        RestoreToolAuthorizationRequest request,
        string canonicalEvidenceDbSha256) =>
        Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        string.Join(
                            ':',
                            ToolId,
                            request.DropOperationId,
                            request.DropPlanDigest,
                            request.OriginalBundleManifestSha256,
                            request.PinnedRestoreToolSha256,
                            request.ValidatorBaseToolSha256,
                            request.AuthorizedRestoreToolSha256,
                            request.AuthorizedArchiveHelperSha256,
                            request.AuthorizerBinarySha256,
                            request.RepairPackageManifestSha256,
                            request.RepositoryCommit,
                            request.RepositoryTreeId,
                            request.PinnedToBaseDiffSha256,
                            request.BaseToFinalDiffSha256,
                            request.SourceManifestSha256,
                            request.TestEvidenceManifestSha256,
                            request.EvidenceSha256,
                            canonicalEvidenceDbSha256))))
            .ToLowerInvariant()[..32];
}

public sealed record RepairPackageFile(
    string Path,
    string Sha256,
    long Bytes);

public sealed record RestoreToolRepairPackageManifest(
    int SchemaVersion,
    string ToolId,
    string Status,
    DateTimeOffset CreatedAtUtc,
    string DropOperationId,
    string DropPlanDigest,
    string DropPlanSha256,
    string DropReportSha256,
    string OriginalBundleManifestSha256,
    string PinnedRestoreToolSha256,
    string ValidatorBaseToolSha256,
    string AuthorizedRestoreToolSha256,
    string AuthorizedArchiveHelperSha256,
    string AuthorizerBinarySha256,
    string RepositoryCommit,
    string RepositoryTreeId,
    string PinnedToBaseDiffSha256,
    string BaseToFinalDiffSha256,
    string SourceManifestSha256,
    string TestEvidenceManifestSha256,
    IReadOnlyList<RepairPackageFile> Files);

public sealed record RestoreToolAuthorizationRequest(
    string DropOperationId,
    string DropPlanDigest,
    string OriginalBundleManifestSha256,
    string PinnedRestoreToolSha256,
    string ValidatorBaseToolSha256,
    string AuthorizedRestoreToolSha256,
    string AuthorizedArchiveHelperSha256,
    string AuthorizerBinarySha256,
    string RepairPackageManifestSha256,
    string RepositoryCommit,
    string RepositoryTreeId,
    string PinnedToBaseDiffSha256,
    string BaseToFinalDiffSha256,
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
        DropJson.Sha256(this);
}

public sealed record RestoreToolAuthorizationRecord(
    string AuthorizationId,
    string DropOperationId,
    string DropPlanDigest,
    string OriginalBundleManifestSha256,
    string PinnedRestoreToolSha256,
    string ValidatorBaseToolSha256,
    string AuthorizedRestoreToolSha256,
    string AuthorizedArchiveHelperSha256,
    string AuthorizerBinarySha256,
    string RepairPackageManifestSha256,
    string RepositoryCommit,
    string RepositoryTreeId,
    string PinnedToBaseDiffSha256,
    string BaseToFinalDiffSha256,
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
    int BackendPid,
    string TransactionId,
    DateTimeOffset AuthorizedAt);

public sealed record RestoreToolAuthorizationReport(
    int SchemaVersion,
    string ToolId,
    string Action,
    string Status,
    DateTimeOffset CompletedAtUtc,
    string AuthorizationId,
    RestoreToolAuthorizationRequest Request,
    RestoreToolAuthorizationRecord DatabaseEvidence,
    string? ReportSha256 = null)
{
    public RestoreToolAuthorizationReport Seal() =>
        this with
        {
            ReportSha256 = DropJson.Sha256(
                this with { ReportSha256 = null }),
        };
}
