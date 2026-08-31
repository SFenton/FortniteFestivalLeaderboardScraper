using System.Text.Json;
using FstSnapshotGenerationDrop;

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
