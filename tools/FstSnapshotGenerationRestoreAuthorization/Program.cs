using System.Text.Json;
using FstSnapshotGenerationDrop;
using FstSnapshotGenerationRestoreContinuation;

namespace FstSnapshotGenerationRestoreAuthorization;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0
            || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }
        try
        {
            using var cancellation =
                new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            return args[0] switch
            {
                "prepare-repair-package" =>
                    Prepare(
                        AuthorizationArguments.Parse(
                            args.Skip(1),
                            [
                                "drop-plan",
                                "drop-report",
                                "original-bundle",
                                "expected-drop-plan-digest",
                                "expected-drop-operation-id",
                                "validator-base-tool",
                                "pinned-to-base-diff",
                                "base-to-final-diff",
                                "source-manifest",
                                "test-evidence-manifest",
                                "test-results",
                                "output",
                            ])),
                "authorize-repair-tool" =>
                    await AuthorizeAsync(
                        AuthorizationArguments.Parse(
                            args.Skip(1),
                            [
                                "drop-plan",
                                "drop-report",
                                "original-bundle",
                                "expected-drop-plan-digest",
                                "expected-drop-operation-id",
                                "repair-package",
                                "reason-code",
                                "reason-text",
                                "approved-by",
                                "reviewed-by",
                                "approval-reference",
                                "output",
                            ]),
                        cancellation.Token),
                "confirm-repair-tool" =>
                    await ConfirmAsync(
                        AuthorizationArguments.Parse(
                            args.Skip(1),
                            [
                                "drop-plan",
                                "expected-drop-plan-digest",
                                "expected-drop-operation-id",
                                "repair-package",
                                "output",
                            ]),
                        cancellation.Token),
                "prepare-continuation-package" =>
                    PrepareContinuation(
                        AuthorizationArguments.Parse(
                            args.Skip(1),
                            [
                                "restore-plan",
                                "restore-report",
                                "predecessor-repair-package",
                                "recovery-bundle",
                                "baseline-route-manifest",
                                "post-restore-route-manifest",
                                "candidate-route-manifest",
                                "predecessor-to-continuation-diff",
                                "source-manifest",
                                "test-evidence-manifest",
                                "test-results",
                                "expected-plan-digest",
                                "expected-operation-id",
                                "output",
                            ])),
                "authorize-continuation-tool" =>
                    await AuthorizeContinuationAsync(
                        AuthorizationArguments.Parse(
                            args.Skip(1),
                            [
                                "restore-plan",
                                "restore-report",
                                "continuation-package",
                                "expected-continuation-package-manifest-sha256",
                                "expected-plan-digest",
                                "expected-operation-id",
                                "reason-code",
                                "reason-text",
                                "approved-by",
                                "reviewed-by",
                                "approval-reference",
                                "output",
                            ]),
                        cancellation.Token),
                "confirm-continuation-tool" =>
                    await ConfirmContinuationAsync(
                        AuthorizationArguments.Parse(
                            args.Skip(1),
                            [
                                "restore-plan",
                                "continuation-package",
                                "expected-continuation-package-manifest-sha256",
                                "expected-plan-digest",
                                "expected-operation-id",
                                "continuation-authorization-id",
                                "output",
                            ]),
                        cancellation.Token),
                _ => throw new ArgumentException(
                    $"Unknown command: {args[0]}"),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"ERROR: {exception.Message}");
            return 1;
        }
    }

    private static int Prepare(
        AuthorizationArguments options)
    {
        var paths = Paths();
        var output = paths.ResolveNewDirectory(
            options.Require("output"));
        var manifest = AuthorizationPackage.Prepare(
            paths.ResolveInputFile(
                options.Require("drop-plan")),
            paths.ResolveInputFile(
                options.Require("drop-report")),
            paths.ResolveInputDirectory(
                options.Require("original-bundle")),
            paths.ResolveInputFile(
                options.Require("validator-base-tool")),
            paths.ResolveInputFile(
                options.Require("pinned-to-base-diff")),
            paths.ResolveInputFile(
                options.Require("base-to-final-diff")),
            paths.ResolveInputFile(
                options.Require("source-manifest")),
            paths.ResolveInputFile(
                options.Require(
                    "test-evidence-manifest")),
            paths.ResolveInputFile(
                options.Require("test-results")),
            options.Require(
                "expected-drop-plan-digest"),
            options.Require(
                "expected-drop-operation-id"),
            output);
        var manifestPath = Path.Combine(
            output,
            "repair-manifest.json");
        Console.WriteLine(
            $"package={output} "
            + $"manifestSha256={DropEvidenceValidator.Sha256File(manifestPath)} "
            + $"authorizedToolSha256={manifest.AuthorizedRestoreToolSha256}");
        return 0;
    }

    private static async Task<int> AuthorizeAsync(
        AuthorizationArguments options,
        CancellationToken ct)
    {
        var paths = Paths();
        var planPath = paths.ResolveInputFile(
            options.Require("drop-plan"));
        var reportPath = paths.ResolveInputFile(
            options.Require("drop-report"));
        var bundle = paths.ResolveInputDirectory(
            options.Require("original-bundle"));
        var package = paths.ResolveInputDirectory(
            options.Require("repair-package"));
        var output = paths.ResolveNewFile(
            options.Require("output"));
        var plan =
            DropEvidenceValidator.ReadStrict<
                SnapshotGenerationDropPlan>(planPath);
        plan.Validate();
        ValidateExpectedPlan(options, plan);
        var report = AuthorizationPackage
            .ReadDropReport(reportPath);
        ValidateReport(plan, report);
        var manifest =
            AuthorizationPackage.Validate(package);
        ValidateManifest(
            plan,
            planPath,
            reportPath,
            bundle,
            package,
            manifest);
        var approvedBy = ValidateActor(
            options.Require("approved-by"),
            "approved-by");
        var reviewedBy = ValidateActor(
            options.Require("reviewed-by"),
            "reviewed-by");
        var approvalReference = ValidateActor(
            options.Require("approval-reference"),
            "approval-reference");
        var reasonCode =
            options.Require("reason-code");
        var reasonText = ValidateActor(
            options.Require("reason-text"),
            "reason-text");
        var packageManifestPath = Path.Combine(
            package,
            "repair-manifest.json");
        var canonicalEvidence =
            JsonSerializer.SerializeToElement(
                new
                {
                    PlanPath = planPath,
                    PlanSha256 =
                        DropEvidenceValidator
                            .Sha256File(planPath),
                    ReportPath = reportPath,
                    ReportSha256 =
                        DropEvidenceValidator
                            .Sha256File(reportPath),
                    OriginalBundlePath = bundle,
                    RepairPackagePath = package,
                    RepairPackageManifestSha256 =
                        DropEvidenceValidator
                            .Sha256File(
                                packageManifestPath),
                    Manifest = manifest,
                });
        var request =
            new RestoreToolAuthorizationRequest(
                plan.DropOperationId!,
                plan.PlanDigest!,
                plan.RecoveryBundleManifestSha256,
                plan.RestoreToolSha256,
                manifest.ValidatorBaseToolSha256,
                manifest.AuthorizedRestoreToolSha256,
                manifest
                    .AuthorizedArchiveHelperSha256,
                manifest.AuthorizerBinarySha256,
                DropEvidenceValidator.Sha256File(
                    packageManifestPath),
                manifest.RepositoryCommit,
                manifest.RepositoryTreeId,
                manifest.PinnedToBaseDiffSha256,
                manifest.BaseToFinalDiffSha256,
                manifest.SourceManifestSha256,
                manifest.TestEvidenceManifestSha256,
                reasonCode,
                reasonText,
                approvedBy,
                reviewedBy,
                approvalReference,
                canonicalEvidence);
        Console.WriteLine(
            $"authorizationEvidenceSha256={request.EvidenceSha256}");
        await using var database =
            AuthorizationDatabase.FromEnvironment();
        var record = await database.AuthorizeAsync(
            request,
            ct);
        var reportValue =
            new RestoreToolAuthorizationReport(
                1,
                RestoreToolAuthorizationContract
                    .AuthorizerToolId,
                "authorize-repair-tool",
                "authorized",
                DateTimeOffset.UtcNow,
                record.AuthorizationId,
                request,
                record).Seal();
        DropEvidenceValidator.WriteNewCanonical(
            output,
            reportValue);
        Console.WriteLine(
            $"authorization={record.AuthorizationId} "
            + $"output={output}");
        return 0;
    }

    private static async Task<int> ConfirmAsync(
        AuthorizationArguments options,
        CancellationToken ct)
    {
        var paths = Paths();
        var planPath = paths.ResolveInputFile(
            options.Require("drop-plan"));
        var package = paths.ResolveInputDirectory(
            options.Require("repair-package"));
        var output = paths.ResolveNewFile(
            options.Require("output"));
        var plan =
            DropEvidenceValidator.ReadStrict<
                SnapshotGenerationDropPlan>(planPath);
        plan.Validate();
        ValidateExpectedPlan(options, plan);
        var manifest =
            AuthorizationPackage.Validate(package);
        await using var database =
            AuthorizationDatabase.FromEnvironment();
        var record = await database.ReadByToolAsync(
            plan.DropOperationId!,
            manifest.AuthorizedRestoreToolSha256,
            ct);
        if (record.DropPlanDigest !=
                plan.PlanDigest
            || record
                .OriginalBundleManifestSha256 !=
                plan.RecoveryBundleManifestSha256
            || record.PinnedRestoreToolSha256 !=
                plan.RestoreToolSha256
            || record.ValidatorBaseToolSha256 !=
                manifest.ValidatorBaseToolSha256
            || record.AuthorizedRestoreToolSha256 !=
                manifest
                    .AuthorizedRestoreToolSha256
            || record
                .AuthorizedArchiveHelperSha256 !=
                manifest
                    .AuthorizedArchiveHelperSha256
            || record.AuthorizerBinarySha256 !=
                manifest.AuthorizerBinarySha256
            || record.RepairPackageManifestSha256 !=
                DropEvidenceValidator.Sha256File(
                    Path.Combine(
                        package,
                        "repair-manifest.json"))
            || record.RepositoryCommit !=
                manifest.RepositoryCommit
            || record.RepositoryTreeId !=
                manifest.RepositoryTreeId
            || record.PinnedToBaseDiffSha256 !=
                manifest.PinnedToBaseDiffSha256
            || record.BaseToFinalDiffSha256 !=
                manifest.BaseToFinalDiffSha256
            || record.SourceManifestSha256 !=
                manifest.SourceManifestSha256
            || record.TestEvidenceManifestSha256 !=
                manifest.TestEvidenceManifestSha256
        )
        {
            throw new InvalidDataException(
                "Authorization confirmation differs from the repair package.");
        }
        var request =
            new RestoreToolAuthorizationRequest(
                record.DropOperationId,
                record.DropPlanDigest,
                record.OriginalBundleManifestSha256,
                record.PinnedRestoreToolSha256,
                record.ValidatorBaseToolSha256,
                record.AuthorizedRestoreToolSha256,
                record.AuthorizedArchiveHelperSha256,
                record.AuthorizerBinarySha256,
                record.RepairPackageManifestSha256,
                record.RepositoryCommit,
                record.RepositoryTreeId,
                record.PinnedToBaseDiffSha256,
                record.BaseToFinalDiffSha256,
                record.SourceManifestSha256,
                record.TestEvidenceManifestSha256,
                record.ReasonCode,
                record.ReasonText,
                record.ApprovedBy,
                record.ReviewedBy,
                record.ApprovalReference,
                record.CanonicalEvidence);
        if (request.EvidenceSha256 !=
            record.EvidenceSha256)
        {
            throw new InvalidDataException(
                "Authorization evidence digest differs.");
        }
        if (RestoreToolAuthorizationContract
                .DeriveAuthorizationId(
                    request,
                    record.CanonicalEvidenceDbSha256)
            != record.AuthorizationId)
        {
            throw new InvalidDataException(
                "Authorization ID differs from recorded database evidence.");
        }
        var report =
            new RestoreToolAuthorizationReport(
                1,
                RestoreToolAuthorizationContract
                    .AuthorizerToolId,
                "confirm-repair-tool",
                "confirmed",
                DateTimeOffset.UtcNow,
                record.AuthorizationId,
                request,
                record).Seal();
        DropEvidenceValidator.WriteNewCanonical(
            output,
            report);
        Console.WriteLine(
            $"authorization={record.AuthorizationId} "
            + $"output={output}");
        return 0;
    }

    private static int PrepareContinuation(
        AuthorizationArguments options)
    {
        var paths = Paths();
        var output = paths.ResolveNewDirectory(
            options.Require("output"));
        var manifest =
            ContinuationAuthorizationPackage.Prepare(
                paths.ResolveInputFile(
                    options.Require("restore-plan")),
                paths.ResolveInputFile(
                    options.Require("restore-report")),
                paths.ResolveInputDirectory(
                    options.Require(
                        "predecessor-repair-package")),
                paths.ResolveInputDirectory(
                    options.Require("recovery-bundle")),
                paths.ResolveInputFile(
                    options.Require(
                        "baseline-route-manifest")),
                paths.ResolveInputFile(
                    options.Require(
                        "post-restore-route-manifest")),
                paths.ResolveInputFile(
                    options.Require(
                        "candidate-route-manifest")),
                paths.ResolveInputFile(
                    options.Require(
                        "predecessor-to-continuation-diff")),
                paths.ResolveInputFile(
                    options.Require(
                        "source-manifest")),
                paths.ResolveInputFile(
                    options.Require(
                        "test-evidence-manifest")),
                paths.ResolveInputFile(
                    options.Require("test-results")),
                options.Require(
                    "expected-plan-digest"),
                options.Require(
                    "expected-operation-id"),
                output);
        var manifestPath = Path.Combine(
            output,
            "continuation-manifest.json");
        Console.WriteLine(
            $"package={output} "
            + $"manifestSha256={ContinuationPackage.Sha256File(manifestPath)} "
            + $"continuationToolSha256={manifest.AuthorizedContinuationToolSha256} "
            + $"evidenceAssemblySha256={manifest.AuthorizedEvidenceAssemblySha256}");
        return 0;
    }

    private static async Task<int>
        AuthorizeContinuationAsync(
            AuthorizationArguments options,
            CancellationToken ct)
    {
        var paths = Paths();
        var planPath = paths.ResolveInputFile(
            options.Require("restore-plan"));
        var reportPath = paths.ResolveInputFile(
            options.Require("restore-report"));
        var package = paths.ResolveInputDirectory(
            options.Require(
                "continuation-package"));
        var output = paths.ResolveNewFile(
            options.Require("output"));
        var manifest =
            ContinuationPackage.Validate(package);
        ValidateContinuationInputs(
            options,
            manifest,
            planPath,
            reportPath,
            package);
        var approvedBy = ValidateActor(
            options.Require("approved-by"),
            "approved-by");
        var reviewedBy = ValidateActor(
            options.Require("reviewed-by"),
            "reviewed-by");
        var approvalReference = ValidateActor(
            options.Require(
                "approval-reference"),
            "approval-reference");
        var reasonCode =
            options.Require("reason-code");
        var reasonText = ValidateActor(
            options.Require("reason-text"),
            "reason-text");
        var packageManifestPath = Path.Combine(
            package,
            "continuation-manifest.json");
        var canonicalEvidence =
            JsonSerializer.SerializeToElement(
                new
                {
                    RestorePlanPath = planPath,
                    RestorePlanFileSha256 =
                        ContinuationPackage
                            .Sha256File(planPath),
                    RestoreReportPath = reportPath,
                    RestoreReportSha256 =
                        ContinuationPackage
                            .Sha256File(reportPath),
                    ContinuationPackagePath =
                        package,
                    ContinuationPackageManifestSha256 =
                        ContinuationPackage.Sha256File(
                            packageManifestPath),
                    Manifest = manifest,
                });
        var request =
            new RestoreContinuationAuthorizationRequest(
                manifest.RestoreOperationId,
                manifest.DropOperationId,
                manifest.PredecessorAuthorizationId,
                manifest.RestorePlanDigest,
                manifest.RestorePlanFileSha256,
                manifest.RestoreReportSha256,
                manifest.PredecessorRestoreToolSha256,
                manifest
                    .PredecessorRepairPackageManifestSha256,
                manifest.RecoveryBundleManifestSha256,
                manifest
                    .AuthorizedContinuationToolSha256,
                manifest
                    .AuthorizedEvidenceAssemblySha256,
                manifest
                    .RouteParityReferenceSourceSha256,
                manifest.AuthorizerBinarySha256,
                ContinuationPackage.Sha256File(
                    packageManifestPath),
                manifest.RouteParityAlgorithmId,
                manifest.RouteParityPreflightSha256,
                manifest.BaselineRouteManifestSha256,
                manifest.BaselineRouteChecksumsSha256,
                manifest.CandidateRouteManifestSha256,
                manifest.CandidateRouteChecksumsSha256,
                manifest.PublicationId,
                manifest.PublishedScrapeId,
                manifest.RepositoryCommit,
                manifest.RepositoryTreeId,
                manifest
                    .PredecessorToContinuationDiffSha256,
                manifest.SourceManifestSha256,
                manifest.TestEvidenceManifestSha256,
                reasonCode,
                reasonText,
                approvedBy,
                reviewedBy,
                approvalReference,
                canonicalEvidence);
        await using var database =
            ContinuationAuthorizationDatabase
                .FromEnvironment();
        var record = await database.AuthorizeAsync(
            request,
            ct);
        var report =
            new RestoreContinuationAuthorizationReport(
                RestoreContinuationContract
                    .SchemaVersion,
                RestoreContinuationContract
                    .AuthorizerToolId,
                "authorize-continuation-tool",
                "authorized",
                DateTimeOffset.UtcNow,
                record.ContinuationAuthorizationId,
                request,
                record).Seal();
        ContinuationPackage.WriteNewCanonical(
            output,
            report);
        Console.WriteLine(
            $"authorization={record.ContinuationAuthorizationId} "
            + $"output={output}");
        return 0;
    }

    private static async Task<int>
        ConfirmContinuationAsync(
            AuthorizationArguments options,
            CancellationToken ct)
    {
        var paths = Paths();
        var planPath = paths.ResolveInputFile(
            options.Require("restore-plan"));
        var package = paths.ResolveInputDirectory(
            options.Require(
                "continuation-package"));
        var output = paths.ResolveNewFile(
            options.Require("output"));
        var manifest =
            ContinuationPackage.Validate(package);
        using var plan =
            ContinuationPackage.ReadJson(planPath);
        var packageManifestSha256 =
            ContinuationPackage.Sha256File(
                Path.Combine(
                    package,
                    "continuation-manifest.json"));
        if (packageManifestSha256 !=
                options.Require(
                    "expected-continuation-package-manifest-sha256")
            || manifest.AuthorizerBinarySha256 !=
                AuthorizationPackage.AuthorizerSha256()
            || manifest.RestorePlanFileSha256 !=
                ContinuationPackage.Sha256File(
                    planPath)
            || manifest.RestoreOperationId !=
                options.Require(
                    "expected-operation-id")
            || manifest.RestorePlanDigest !=
                options.Require(
                    "expected-plan-digest")
            || ContinuationDatabase.RequireString(
                    plan.RootElement,
                    "restoreOperationId") !=
                manifest.RestoreOperationId)
        {
            throw new InvalidDataException(
                "Continuation confirmation inputs differ.");
        }
        var authorizationId = options.Require(
            "continuation-authorization-id");
        await using var database =
            ContinuationAuthorizationDatabase
                .FromEnvironment();
        var record = await database.ReadAsync(
            manifest.RestoreOperationId,
            authorizationId,
            ct);
        var request =
            RequestFromRecord(record);
        if (record.ContinuationPackageManifestSha256 !=
                packageManifestSha256
            || record.AuthorizedContinuationToolSha256 !=
                manifest.AuthorizedContinuationToolSha256
            || record.AuthorizedEvidenceAssemblySha256 !=
                manifest.AuthorizedEvidenceAssemblySha256
            || record.RouteParityPreflightSha256 !=
                manifest.RouteParityPreflightSha256
            || request.EvidenceSha256 !=
                record.EvidenceSha256
            || RestoreContinuationContract
                    .DeriveAuthorizationId(
                        request,
                        record
                            .CanonicalEvidenceDbSha256) !=
                record.ContinuationAuthorizationId)
        {
            throw new InvalidDataException(
                "Continuation authorization differs from its package.");
        }
        var report =
            new RestoreContinuationAuthorizationReport(
                RestoreContinuationContract
                    .SchemaVersion,
                RestoreContinuationContract
                    .AuthorizerToolId,
                "confirm-continuation-tool",
                "confirmed",
                DateTimeOffset.UtcNow,
                record.ContinuationAuthorizationId,
                request,
                record).Seal();
        ContinuationPackage.WriteNewCanonical(
            output,
            report);
        Console.WriteLine(
            $"authorization={record.ContinuationAuthorizationId} "
            + $"output={output}");
        return 0;
    }

    private static void ValidateContinuationInputs(
        AuthorizationArguments options,
        RestoreContinuationPackageManifest manifest,
        string planPath,
        string reportPath,
        string package)
    {
        var packageManifestSha256 =
            ContinuationPackage.Sha256File(
                Path.Combine(
                    package,
                    "continuation-manifest.json"));
        if (manifest.RestorePlanDigest !=
                options.Require(
                    "expected-plan-digest")
            || manifest.RestoreOperationId !=
                options.Require(
                    "expected-operation-id")
            || packageManifestSha256 !=
                options.Require(
                    "expected-continuation-package-manifest-sha256")
            || manifest.RestorePlanFileSha256 !=
                ContinuationPackage.Sha256File(
                    planPath)
            || manifest.RestoreReportSha256 !=
                ContinuationPackage.Sha256File(
                    reportPath)
            || manifest.AuthorizerBinarySha256 !=
                AuthorizationPackage
                    .AuthorizerSha256()
            || manifest.AuthorizedContinuationToolSha256 !=
                ContinuationPackage.Sha256File(
                    Path.Combine(
                        package,
                        "runtime",
                        "FstSnapshotGenerationRestoreContinuation.dll"))
            || manifest.AuthorizedEvidenceAssemblySha256 !=
                ContinuationPackage.Sha256File(
                    Path.Combine(
                        package,
                        "runtime",
                        "FstSnapshotGenerationEvidence.dll")))
        {
            throw new InvalidDataException(
                "Continuation package differs from immutable restore evidence.");
        }
    }

    private static
        RestoreContinuationAuthorizationRequest
        RequestFromRecord(
            RestoreContinuationAuthorizationRecord record) =>
        new(
            record.RestoreOperationId,
            record.DropOperationId,
            record.PredecessorAuthorizationId,
            record.RestorePlanDigest,
            record.RestorePlanFileSha256,
            record.RestoreReportSha256,
            record.PredecessorRestoreToolSha256,
            record
                .PredecessorRepairPackageManifestSha256,
            record.RecoveryBundleManifestSha256,
            record.AuthorizedContinuationToolSha256,
            record.AuthorizedEvidenceAssemblySha256,
            record.RouteParityReferenceSourceSha256,
            record.AuthorizerBinarySha256,
            record.ContinuationPackageManifestSha256,
            record.RouteParityAlgorithmId,
            record.RouteParityPreflightSha256,
            record.BaselineRouteManifestSha256,
            record.BaselineRouteChecksumsSha256,
            record.CandidateRouteManifestSha256,
            record.CandidateRouteChecksumsSha256,
            record.PublicationId,
            record.PublishedScrapeId,
            record.RepositoryCommit,
            record.RepositoryTreeId,
            record
                .PredecessorToContinuationDiffSha256,
            record.SourceManifestSha256,
            record.TestEvidenceManifestSha256,
            record.ReasonCode,
            record.ReasonText,
            record.ApprovedBy,
            record.ReviewedBy,
            record.ApprovalReference,
            record.CanonicalEvidence);

    private static void ValidateExpectedPlan(
        AuthorizationArguments options,
        SnapshotGenerationDropPlan plan)
    {
        if (plan.PlanDigest != options.Require(
                "expected-drop-plan-digest")
            || plan.DropOperationId != options.Require(
                "expected-drop-operation-id"))
        {
            throw new InvalidDataException(
                "Drop plan identity differs from expected.");
        }
    }

    private static void ValidateReport(
        SnapshotGenerationDropPlan plan,
        SnapshotGenerationDropExecutionReport report)
    {
        if (report.DropOperationId !=
                plan.DropOperationId
            || report.PlanDigest != plan.PlanDigest
            || report.Status != "dropped"
            || report.Action is not (
                "drop" or "confirm"))
        {
            throw new InvalidDataException(
                "Committed drop report differs from its plan.");
        }
    }

    private static void ValidateManifest(
        SnapshotGenerationDropPlan plan,
        string planPath,
        string reportPath,
        string bundle,
        string package,
        RestoreToolRepairPackageManifest manifest)
    {
        var bundleManifestPath = Path.Combine(
            bundle,
            "bundle-manifest.json");
        _ = DropEvidenceValidator
            .ValidateRecoveryBundle(bundle);
        if (manifest.DropOperationId !=
                plan.DropOperationId
            || manifest.DropPlanDigest !=
                plan.PlanDigest
            || manifest.DropPlanSha256 !=
                DropEvidenceValidator.Sha256File(
                    planPath)
            || manifest.DropReportSha256 !=
                DropEvidenceValidator.Sha256File(
                    reportPath)
            || manifest
                .OriginalBundleManifestSha256 !=
                plan.RecoveryBundleManifestSha256
            || DropEvidenceValidator.Sha256File(
                    bundleManifestPath) !=
                plan.RecoveryBundleManifestSha256
            || manifest.PinnedRestoreToolSha256 !=
                plan.RestoreToolSha256
            || DropEvidenceValidator.Sha256File(
                    Path.Combine(
                        bundle,
                        "restore-tool.py")) !=
                plan.RestoreToolSha256
            || DropEvidenceValidator.Sha256File(
                    Path.Combine(
                        bundle,
                        "postgres-snapshot-generation-archive.py")) !=
                manifest
                    .AuthorizedArchiveHelperSha256
            || manifest.AuthorizerBinarySha256 !=
                AuthorizationPackage
                    .AuthorizerSha256()
            || manifest.AuthorizedRestoreToolSha256 !=
                DropEvidenceValidator.Sha256File(
                    Path.Combine(
                        package,
                        "restore-tool.py")))
        {
            throw new InvalidDataException(
                "Repair package differs from the committed drop evidence.");
        }
    }

    private static DropEvidencePaths Paths()
    {
        var value = Environment.GetEnvironmentVariable(
            "FST_SNAPSHOT_RESTORE_AUTHORIZATION_EVIDENCE_ROOT");
        return new DropEvidencePaths(value ?? "");
    }

    private static string ValidateActor(
        string value,
        string label)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 512
            || value.Any(character =>
                char.IsControl(character)))
        {
            throw new ArgumentException(
                $"{label} is invalid.");
        }
        return value;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            Snapshot-generation restore-tool authorizer

            Commands:
              prepare-repair-package  Build a sealed tool-only repair package.
              authorize-repair-tool   Insert one exact immutable authorization.
              confirm-repair-tool     Confirm an authorization after uncertain commit.
              prepare-continuation-package
                                      Build a continuation-only evidence package.
              authorize-continuation-tool
                                      Insert one exact post-restore authorization.
              confirm-continuation-tool
                                      Confirm post-restore authorization after uncertainty.

            This tool has no Docker, restore, relation, schema, SQL, or automatic target surface.
            """);
    }
}

public sealed class AuthorizationArguments
{
    private readonly IReadOnlyDictionary<
        string,
        string> _values;

    private AuthorizationArguments(
        IReadOnlyDictionary<string, string> values)
    {
        _values = values;
    }

    public static AuthorizationArguments Parse(
        IEnumerable<string> arguments,
        IReadOnlyCollection<string> required)
    {
        var values = new Dictionary<string, string>(
            StringComparer.Ordinal);
        using var enumerator =
            arguments.GetEnumerator();
        while (enumerator.MoveNext())
        {
            var option = enumerator.Current;
            if (!option.StartsWith(
                    "--",
                    StringComparison.Ordinal)
                || option.Length <= 2
                || !required.Contains(
                    option[2..],
                    StringComparer.Ordinal)
                || !enumerator.MoveNext()
                || !values.TryAdd(
                    option[2..],
                    enumerator.Current))
            {
                throw new ArgumentException(
                    $"Invalid or duplicate option: {option}");
            }
        }
        var missing = required
            .Where(key => !values.ContainsKey(key))
            .ToArray();
        if (missing.Length != 0)
        {
            throw new ArgumentException(
                $"Missing option(s): {string.Join(", ", missing)}");
        }
        return new AuthorizationArguments(values);
    }

    public string Require(string key) =>
        _values.TryGetValue(key, out var value)
            && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new ArgumentException(
                    $"--{key} is required.");
}
