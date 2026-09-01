using System.Text.Json;
using FstSnapshotGenerationQuarantine;

namespace FstSnapshotGenerationRestoreContinuation;

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
                "confirm" =>
                    await ConfirmAsync(
                        ContinuationArguments.Parse(
                            args.Skip(1),
                            CommonOptions),
                        cancellation.Token),
                "attest" =>
                    await AttestAsync(
                        ContinuationArguments.Parse(
                            args.Skip(1),
                            CommonOptions.Append(
                                "attested-by")),
                        cancellation.Token),
                "finalize" =>
                    await FinalizeAsync(
                        ContinuationArguments.Parse(
                            args.Skip(1),
                            CommonOptions.Concat(
                            [
                                "finalized-by",
                                "finalize-reference",
                            ])),
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

    private static readonly string[] CommonOptions =
    [
        "package",
        "expected-continuation-package-manifest-sha256",
        "restore-plan",
        "restore-report",
        "expected-plan-digest",
        "expected-operation-id",
        "continuation-authorization-id",
        "output",
    ];

    private static async Task<int> ConfirmAsync(
        ContinuationArguments options,
        CancellationToken ct)
    {
        var context = LoadContext(options);
        await using var database =
            ContinuationDatabase.FromEnvironment();
        var state = await database.ReadStateAsync(
            context.Manifest.RestoreOperationId,
            context.AuthorizationId,
            ct);
        ValidateState(context, state);
        var report =
            new RestoreContinuationCommandReport(
                RestoreContinuationContract
                    .SchemaVersion,
                RestoreContinuationContract.ToolId,
                "confirm",
                state.GetProperty("finalized")
                    .GetBoolean()
                    ? "finalized"
                    : state.GetProperty("attested")
                        .GetBoolean()
                        ? "attested"
                        : "restored",
                DateTimeOffset.UtcNow,
                context.Manifest.RestoreOperationId,
                context.AuthorizationId,
                context.Manifest
                    .AuthorizedContinuationToolSha256,
                state).Seal();
        ContinuationPackage.WriteNewCanonical(
            context.Output,
            report);
        Console.WriteLine(
            $"restore={context.Manifest.RestoreOperationId} "
            + $"authorization={context.AuthorizationId} "
            + $"output={context.Output}");
        return 0;
    }

    private static async Task<int> AttestAsync(
        ContinuationArguments options,
        CancellationToken ct)
    {
        var context = LoadContext(options);
        var attestedBy = ValidateActor(
            options.Require("attested-by"),
            "attested-by");
        var historicalBridge =
            QuarantineEvidenceValidator
                .ValidateShopDailyInventoryRolloverBridge(
                    context.Manifest
                        .HistoricalBaselineRouteManifestPath,
                    context.Manifest
                        .BaselineRouteManifestPath);
        var detailed =
            QuarantineEvidenceValidator
                .ValidateDetailedRouteParity(
                    context.Manifest
                        .BaselineRouteManifestPath,
                    context.Manifest
                        .CandidateRouteManifestPath);
        QuarantineEvidenceValidator
            .ValidateStabilizedShopRefresh(
                context.Manifest
                    .BaselineRouteManifestPath,
                context.Manifest
                    .CandidateRouteManifestPath,
                historicalBridge
                    .StabilizedShopLastUpdatedUtc);
        ValidateParity(
            context,
            historicalBridge,
            detailed);
        await using var database =
            ContinuationDatabase.FromEnvironment();
        var current = await database.ReadStateAsync(
            context.Manifest.RestoreOperationId,
            context.AuthorizationId,
            ct);
        ValidateState(context, current);
        if (current.GetProperty("attested")
                .GetBoolean())
        {
            var confirmed =
                new RestoreContinuationCommandReport(
                    RestoreContinuationContract
                        .SchemaVersion,
                    RestoreContinuationContract.ToolId,
                    "attest",
                    "accepted",
                    DateTimeOffset.UtcNow,
                    context.Manifest
                        .RestoreOperationId,
                    context.AuthorizationId,
                    context.Manifest
                        .AuthorizedContinuationToolSha256,
                    current,
                    detailed,
                    historicalBridge).Seal();
            ContinuationPackage.WriteNewCanonical(
                context.Output,
                confirmed);
            return 0;
        }
        var result = await database.AttestAsync(
            context.Manifest,
            context.AuthorizationId,
            historicalBridge,
            detailed,
            attestedBy,
            ct);
        var databaseEvidence =
            JsonSerializer.SerializeToElement(
                new
                {
                    result.State,
                    result.Fingerprint,
                    result.EvidenceSha256,
                });
        var report =
            new RestoreContinuationCommandReport(
                RestoreContinuationContract
                    .SchemaVersion,
                RestoreContinuationContract.ToolId,
                "attest",
                "accepted",
                DateTimeOffset.UtcNow,
                context.Manifest.RestoreOperationId,
                context.AuthorizationId,
                context.Manifest
                    .AuthorizedContinuationToolSha256,
                databaseEvidence,
                detailed,
                historicalBridge).Seal();
        ContinuationPackage.WriteNewCanonical(
            context.Output,
            report);
        Console.WriteLine(
            $"restore={context.Manifest.RestoreOperationId} "
            + $"attestation=accepted "
            + $"output={context.Output}");
        return 0;
    }

    private static async Task<int> FinalizeAsync(
        ContinuationArguments options,
        CancellationToken ct)
    {
        var context = LoadContext(options);
        var finalizedBy = ValidateActor(
            options.Require("finalized-by"),
            "finalized-by");
        var finalizeReference = ValidateActor(
            options.Require("finalize-reference"),
            "finalize-reference");
        await using var database =
            ContinuationDatabase.FromEnvironment();
        var before = await database.ReadStateAsync(
            context.Manifest.RestoreOperationId,
            context.AuthorizationId,
            ct);
        ValidateState(context, before);
        if (before.GetProperty("finalized")
                .GetBoolean())
        {
            var confirmed =
                new RestoreContinuationCommandReport(
                    RestoreContinuationContract
                        .SchemaVersion,
                    RestoreContinuationContract.ToolId,
                    "finalize",
                    "finalized",
                    DateTimeOffset.UtcNow,
                    context.Manifest
                        .RestoreOperationId,
                    context.AuthorizationId,
                    context.Manifest
                        .AuthorizedContinuationToolSha256,
                    before).Seal();
            ContinuationPackage.WriteNewCanonical(
                context.Output,
                confirmed);
            return 0;
        }
        if (!before.GetProperty("attested")
                .GetBoolean())
        {
            throw new InvalidDataException(
                "Continuation finalization requires one unfinalized attestation.");
        }
        var evidence =
            JsonSerializer.SerializeToElement(
                new
                {
                    ConfirmedRestore = before,
                    ContinuationAuthorizationId =
                        context.AuthorizationId,
                    EvidenceToolSha256 =
                        context.Manifest
                            .AuthorizedContinuationToolSha256,
                });
        await database.FinalizeAsync(
            context.Manifest,
            context.AuthorizationId,
            finalizedBy,
            finalizeReference,
            evidence,
            ct);
        var after = await database.ReadStateAsync(
            context.Manifest.RestoreOperationId,
            context.AuthorizationId,
            ct);
        if (!after.GetProperty("finalized")
                .GetBoolean()
            || after.GetProperty("holdActive")
                .GetBoolean())
        {
            throw new InvalidDataException(
                "Continuation finalization did not commit exact terminal state.");
        }
        var report =
            new RestoreContinuationCommandReport(
                RestoreContinuationContract
                    .SchemaVersion,
                RestoreContinuationContract.ToolId,
                "finalize",
                "finalized",
                DateTimeOffset.UtcNow,
                context.Manifest.RestoreOperationId,
                context.AuthorizationId,
                context.Manifest
                    .AuthorizedContinuationToolSha256,
                after).Seal();
        ContinuationPackage.WriteNewCanonical(
            context.Output,
            report);
        Console.WriteLine(
            $"restore={context.Manifest.RestoreOperationId} "
            + $"status=finalized "
            + $"output={context.Output}");
        return 0;
    }

    private static ContinuationContext LoadContext(
        ContinuationArguments options)
    {
        var paths = Paths();
        var package = paths.ResolveInputDirectory(
            options.Require("package"));
        var packageManifestPath = Path.Combine(
            package,
            "continuation-manifest.json");
        if (ContinuationPackage.Sha256File(
                packageManifestPath) !=
            options.Require(
                "expected-continuation-package-manifest-sha256"))
        {
            throw new InvalidDataException(
                "Continuation package manifest differs from expected.");
        }
        var manifest =
            ContinuationPackage.Validate(package);
        var planPath = paths.ResolveInputFile(
            options.Require("restore-plan"));
        var reportPath = paths.ResolveInputFile(
            options.Require("restore-report"));
        var output = paths.ResolveNewFile(
            options.Require("output"));
        var predecessorPackage =
            paths.ResolveInputDirectory(
                manifest.PredecessorRepairPackagePath);
        var recoveryBundle =
            paths.ResolveInputDirectory(
                manifest.RecoveryBundlePath);
        var baselineManifest =
            paths.ResolveInputFile(
                manifest.BaselineRouteManifestPath);
        var historicalBaselineManifest =
            paths.ResolveInputFile(
                manifest
                    .HistoricalBaselineRouteManifestPath);
        var candidateManifest =
            paths.ResolveInputFile(
                manifest.CandidateRouteManifestPath);
        var serviceRuntimeIsolation =
            paths.ResolveInputFile(
                manifest
                    .ServiceRuntimeIsolationEvidencePath);
        var historicalBaselineChecksums =
            paths.ResolveInputFile(
                Path.Combine(
                    Path.GetDirectoryName(
                        historicalBaselineManifest)!,
                    "SHA256SUMS"));
        var baselineChecksums =
            paths.ResolveInputFile(
                Path.Combine(
                    Path.GetDirectoryName(
                        baselineManifest)!,
                    "SHA256SUMS"));
        var candidateChecksums =
            paths.ResolveInputFile(
                Path.Combine(
                    Path.GetDirectoryName(
                        candidateManifest)!,
                    "SHA256SUMS"));
        if (Path.GetFullPath(planPath) !=
                Path.GetFullPath(
                    manifest.RestorePlanPath)
            || Path.GetFullPath(reportPath) !=
                Path.GetFullPath(
                    manifest.RestoreReportPath)
            || ContinuationPackage.Sha256File(
                    planPath) !=
                manifest.RestorePlanFileSha256
            || ContinuationPackage.Sha256File(
                    reportPath) !=
                manifest.RestoreReportSha256
            || manifest.RestorePlanDigest !=
                options.Require(
                    "expected-plan-digest")
            || manifest.RestoreOperationId !=
                options.Require(
                    "expected-operation-id")
            || manifest.AuthorizedContinuationToolSha256 !=
                ContinuationPackage
                    .CurrentToolSha256()
            || manifest.AuthorizedEvidenceAssemblySha256 !=
                ContinuationPackage
                    .CurrentEvidenceAssemblySha256()
            || ContinuationPackage.Sha256File(
                    Path.Combine(
                        predecessorPackage,
                        "repair-manifest.json")) !=
                manifest
                    .PredecessorRepairPackageManifestSha256
            || ContinuationPackage.Sha256File(
                    Path.Combine(
                        recoveryBundle,
                        "bundle-manifest.json")) !=
                manifest.RecoveryBundleManifestSha256
            || ContinuationPackage.Sha256File(
                    baselineManifest) !=
                manifest.BaselineRouteManifestSha256
            || ContinuationPackage.Sha256File(
                    baselineChecksums) !=
                manifest.BaselineRouteChecksumsSha256
            || ContinuationPackage.Sha256File(
                    candidateManifest) !=
                manifest.CandidateRouteManifestSha256
            || ContinuationPackage.Sha256File(
                    candidateChecksums) !=
                manifest.CandidateRouteChecksumsSha256
            || ContinuationPackage.Sha256File(
                    historicalBaselineManifest) !=
                manifest
                    .HistoricalBaselineRouteManifestSha256
            || ContinuationPackage.Sha256File(
                    historicalBaselineChecksums) !=
                manifest
                    .HistoricalBaselineRouteChecksumsSha256
            || ContinuationPackage.Sha256File(
                    serviceRuntimeIsolation) !=
                manifest
                    .ServiceRuntimeIsolationEvidenceSha256)
        {
            throw new InvalidDataException(
                "Continuation command inputs differ from the sealed package.");
        }
        ValidatePlanAndReport(
            manifest,
            planPath,
            reportPath);
        var preflight =
            ContinuationPackage.ReadStrict<
                RestoreContinuationPreflightReport>(
                Path.Combine(
                    package,
                    "route-parity-preflight.json"));
        if (preflight.ReportSha256 is null
            || preflight.Seal().ReportSha256 !=
                preflight.ReportSha256
            || preflight.RouteParityAlgorithmId !=
                manifest.RouteParityAlgorithmId
            || preflight.RouteParityReferenceSourceSha256 !=
                manifest.RouteParityReferenceSourceSha256
            || preflight.EvidenceAssemblySha256 !=
                manifest.AuthorizedEvidenceAssemblySha256
            || preflight.HistoricalTemporalBridge
                    .HistoricalBaselineManifestSha256 !=
                manifest
                    .HistoricalBaselineRouteManifestSha256
            || preflight.StabilizedParity
                    .RouteSemanticEvidenceSha256 !=
                manifest
                    .StabilizedRouteSemanticEvidenceSha256
            || preflight.HistoricalTemporalBridge
                    .HistoricalCandidateManifestSha256 !=
                manifest.BaselineRouteManifestSha256
            || preflight.HistoricalTemporalBridge
                    .PredicateId !=
                manifest.TemporalBridgePredicateId
            || QuarantineJson.Sha256(
                    preflight
                        .HistoricalTemporalBridge) !=
                manifest.TemporalBridgeEvidenceSha256
            || preflight.RestoreScopeIsolation
                    .EvidenceSha256 !=
                manifest
                    .RestoreScopeIsolationEvidenceSha256
            || preflight
                    .ServiceRuntimeIsolationEvidenceSha256 !=
                manifest
                    .ServiceRuntimeIsolationEvidenceSha256
            || preflight.StabilizedParity
                    .Parity
                    .BaselineManifestSha256 !=
                manifest.BaselineRouteManifestSha256
            || preflight.StabilizedParity
                    .Parity
                    .CandidateManifestSha256 !=
                manifest.CandidateRouteManifestSha256
            || preflight.StabilizedParity
                    .Parity
                    .PublicationId !=
                manifest.PublicationId
            || preflight.StabilizedParity
                    .Parity
                    .PublishedScrapeId !=
                manifest.PublishedScrapeId)
        {
            throw new InvalidDataException(
                "Continuation route preflight is invalid.");
        }
        var authorizationId = options.Require(
            "continuation-authorization-id");
        ValidateHex(
            authorizationId,
            32,
            "Continuation authorization ID");
        return new ContinuationContext(
            package,
            manifest,
            preflight,
            authorizationId,
            output);
    }

    private static void ValidatePlanAndReport(
        RestoreContinuationPackageManifest manifest,
        string planPath,
        string reportPath)
    {
        using var plan =
            ContinuationPackage.ReadJson(planPath);
        using var report =
            ContinuationPackage.ReadJson(reportPath);
        var planRoot = plan.RootElement;
        var reportRoot = report.RootElement;
        var repository = planRoot.GetProperty(
            "repository");
        var predecessor =
            planRoot.GetProperty(
                "restoreToolAuthorization");
        if (ContinuationDatabase.RequireString(
                planRoot,
                "restoreOperationId") !=
                manifest.RestoreOperationId
            || ContinuationDatabase.RequireString(
                planRoot,
                "dropOperationId") !=
                manifest.DropOperationId
            || ContinuationDatabase.RequireString(
                planRoot,
                "planDigest") !=
                manifest.RestorePlanDigest
            || ContinuationDatabase.RequireString(
                repository,
                "toolSha256") !=
                manifest.PredecessorRestoreToolSha256
            || ContinuationDatabase.RequireString(
                predecessor,
                "authorizationId") !=
                manifest.PredecessorAuthorizationId
            || ContinuationDatabase.RequireString(
                predecessor,
                "executingToolSha256") !=
                manifest.PredecessorRestoreToolSha256
            || ContinuationDatabase.RequireString(
                predecessor,
                "repairPackageManifestSha256") !=
                manifest
                    .PredecessorRepairPackageManifestSha256
            || ContinuationDatabase.RequireString(
                reportRoot,
                "restoreOperationId") !=
                manifest.RestoreOperationId
            || ContinuationDatabase.RequireString(
                reportRoot,
                "dropOperationId") !=
                manifest.DropOperationId
            || ContinuationDatabase.RequireString(
                reportRoot,
                "planDigest") !=
                manifest.RestorePlanDigest
            || ContinuationDatabase.RequireString(
                reportRoot,
                "action") != "restore"
            || ContinuationDatabase.RequireString(
                reportRoot,
                "status") != "restored"
            || ContinuationDatabase.RequireString(
                reportRoot,
                "commitOutcome") != "committed")
        {
            throw new InvalidDataException(
                "Immutable H5 plan or report differs from the continuation package.");
        }
    }

    private static void ValidateState(
        ContinuationContext context,
        JsonElement state)
    {
        var manifest = context.Manifest;
        if (ContinuationDatabase.RequireString(
                state,
                "restoreOperationId") !=
                manifest.RestoreOperationId
            || ContinuationDatabase.RequireString(
                state,
                "dropOperationId") !=
                manifest.DropOperationId
            || ContinuationDatabase.RequireString(
                state,
                "planDigest") !=
                manifest.RestorePlanDigest
            || ContinuationDatabase.RequireString(
                state,
                "continuationAuthorizationId") !=
                context.AuthorizationId
            || ContinuationDatabase.RequireString(
                state,
                "restorePlanFileSha256") !=
                manifest.RestorePlanFileSha256
            || ContinuationDatabase.RequireString(
                state,
                "restoreReportSha256") !=
                manifest.RestoreReportSha256
            || ContinuationDatabase.RequireString(
                state,
                "continuationPackageManifestSha256") !=
                ContinuationPackage.Sha256File(
                    Path.Combine(
                        context.Package,
                        "continuation-manifest.json"))
            || ContinuationDatabase.RequireString(
                state,
                "stabilizedRouteSemanticEvidenceSha256") !=
                manifest
                    .StabilizedRouteSemanticEvidenceSha256
            || ContinuationDatabase.RequireString(
                state,
                "temporalBridgePredicateId") !=
                manifest.TemporalBridgePredicateId
            || ContinuationDatabase.RequireString(
                state,
                "temporalBridgeEvidenceSha256") !=
                manifest.TemporalBridgeEvidenceSha256
            || ContinuationDatabase.RequireString(
                state,
                "restoreScopeIsolationEvidenceSha256") !=
                manifest
                    .RestoreScopeIsolationEvidenceSha256
            || ContinuationDatabase.RequireString(
                state,
                "serviceRuntimeIsolationEvidenceSha256") !=
                manifest
                    .ServiceRuntimeIsolationEvidenceSha256
            || ContinuationDatabase.RequireString(
                state,
                "historicalBaselineRouteManifestSha256") !=
                manifest
                    .HistoricalBaselineRouteManifestSha256
            || ContinuationDatabase.RequireString(
                state,
                "baselineRouteManifestSha256") !=
                manifest.BaselineRouteManifestSha256
            || ContinuationDatabase.RequireString(
                state,
                "candidateRouteManifestSha256") !=
                manifest.CandidateRouteManifestSha256)
        {
            throw new InvalidDataException(
                "Continuation database state differs from sealed evidence.");
        }
        var finalized =
            state.GetProperty("finalized")
                .GetBoolean();
        if (!state.GetProperty(
                    "originalIdentityMatches")
                .GetBoolean()
            || state.GetProperty(
                    "attachedIndexCount")
                .GetInt32() != 2
            || state.GetProperty("oldOidExists")
                .GetBoolean()
            || state.GetProperty(
                    "defaultFencePresent")
                .GetBoolean()
            || state.GetProperty("defaultRowCount")
                .GetInt64() != 0
            || state.GetProperty(
                    "currentPublicationId")
                .GetInt64() !=
                manifest.PublicationId
            || state.GetProperty(
                    "currentPublishedScrapeId")
                .GetInt64() !=
                manifest.PublishedScrapeId
            || state.GetProperty(
                    "publicReadsFrozen")
                .GetBoolean()
            || state.GetProperty(
                    "workingPublicationId")
                .ValueKind !=
                JsonValueKind.Null
            || state.GetProperty(
                    "publicationCommitIntentActive")
                .GetBoolean()
            || state.GetProperty(
                    "maxScoreMutationGateActive")
                .GetBoolean()
            || state.GetProperty("runningScrape")
                .GetBoolean()
            || !state.GetProperty("workerOffline")
                .GetBoolean()
            || state.GetProperty("holdActive")
                    .GetBoolean() == finalized
            || state.GetProperty(
                    "mutationGuardPresent")
                    .GetBoolean() == finalized)
        {
            throw new InvalidDataException(
                "Continuation live database gates are unsafe.");
        }
        if (state.GetProperty("attested")
                .GetBoolean()
            && (
                ContinuationDatabase.RequireString(
                    state,
                    "attestationAuthorizationId") !=
                    context.AuthorizationId
                || ContinuationDatabase.RequireString(
                    state,
                    "attestationEvidenceToolSha256") !=
                    manifest
                        .AuthorizedContinuationToolSha256))
        {
            throw new InvalidDataException(
                "Restore attestation is bound to another continuation tool.");
        }
        if (state.GetProperty("finalized")
                .GetBoolean()
            && (
                ContinuationDatabase.RequireString(
                    state,
                    "finalizationAuthorizationId") !=
                    context.AuthorizationId
                || ContinuationDatabase.RequireString(
                    state,
                    "finalizationEvidenceToolSha256") !=
                    manifest
                        .AuthorizedContinuationToolSha256))
        {
            throw new InvalidDataException(
                "Restore finalization is bound to another continuation tool.");
        }
        var authorizedAt =
            state.GetProperty("authorizedAt")
                .GetDateTimeOffset();
        var age = DateTimeOffset.UtcNow -
            authorizedAt;
        if (age > TimeSpan.FromHours(24))
        {
            Console.Error.WriteLine(
                $"WARNING: continuation authorization is {age.TotalHours:F1} hours old; current-state gates still apply.");
        }
    }

    private static void ValidateParity(
        ContinuationContext context,
        ShopDailyInventoryRolloverEvidence bridge,
        DetailedRouteParityEvidence detailed)
    {
        var manifest = context.Manifest;
        var preflight = context.Preflight;
        if (detailed.AlgorithmId !=
                manifest.RouteParityAlgorithmId
            || !detailed.SemanticBinaryParity
            || detailed.Parity.PublicationId !=
                manifest.PublicationId
            || detailed.Parity.PublishedScrapeId !=
                manifest.PublishedScrapeId
            || detailed.Parity.BaselineManifestSha256 !=
                manifest.BaselineRouteManifestSha256
            || detailed.Parity.CandidateManifestSha256 !=
                manifest.CandidateRouteManifestSha256
            || detailed.RouteSemanticEvidenceSha256 !=
                preflight.StabilizedParity
                    .RouteSemanticEvidenceSha256
            || detailed.RouteSemanticEvidenceSha256 !=
                manifest
                    .StabilizedRouteSemanticEvidenceSha256)
        {
            throw new InvalidDataException(
                "Current route parity differs from the authorized preflight.");
        }
        if (bridge.PredicateId !=
                manifest.TemporalBridgePredicateId
            || bridge
                    .HistoricalBaselineManifestSha256 !=
                manifest
                    .HistoricalBaselineRouteManifestSha256
            || bridge
                    .HistoricalCandidateManifestSha256 !=
                manifest.BaselineRouteManifestSha256
            || QuarantineJson.Sha256(bridge) !=
                manifest.TemporalBridgeEvidenceSha256
            || bridge !=
                preflight.HistoricalTemporalBridge)
        {
            throw new InvalidDataException(
                "Historical temporal bridge differs from the authorized preflight.");
        }
        var band = detailed.Routes.Single(
            route => route.Name == "band-export");
        var player = detailed.Routes.Single(
            route => route.Name == "player-export");
        if (band.BaselineSemanticSha256 !=
                preflight.BandExportSemanticSha256
            || player.BaselineSemanticSha256 !=
                preflight.PlayerExportSemanticSha256)
        {
            throw new InvalidDataException(
                "Current export semantics differ from the authorized preflight.");
        }
    }

    private static ContinuationEvidencePaths Paths()
    {
        var root = Environment.GetEnvironmentVariable(
            "FST_SNAPSHOT_RESTORE_CONTINUATION_EVIDENCE_ROOT");
        return new ContinuationEvidencePaths(root ?? "");
    }

    private static string ValidateActor(
        string value,
        string label)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 512
            || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"{label} is invalid.");
        }
        return value;
    }

    private static void ValidateHex(
        string value,
        int length,
        string label)
    {
        if (value.Length != length
            || value.Any(character =>
                character is not (
                    >= '0' and <= '9'
                    or >= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                $"{label} is invalid.");
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            Snapshot-generation restore continuation

            Commands:
              confirm
              attest
              finalize

            This tool has no plan, restore, load, attach, Docker, authorization,
            arbitrary SQL, relation, schema, instrument, or snapshot surface.
            """);
    }

    private sealed record ContinuationContext(
        string Package,
        RestoreContinuationPackageManifest Manifest,
        RestoreContinuationPreflightReport Preflight,
        string AuthorizationId,
        string Output);
}

public sealed class ContinuationArguments
{
    private readonly IReadOnlyDictionary<string, string>
        _values;

    private ContinuationArguments(
        IReadOnlyDictionary<string, string> values)
    {
        _values = values;
    }

    public static ContinuationArguments Parse(
        IEnumerable<string> arguments,
        IEnumerable<string> requiredOptions)
    {
        var required = requiredOptions.ToHashSet(
            StringComparer.Ordinal);
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
                || !required.Contains(option[2..])
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
            .Where(option =>
                !values.ContainsKey(option))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new ArgumentException(
                "Missing options: "
                + string.Join(", ", missing));
        }
        return new ContinuationArguments(values);
    }

    public string Require(string name) =>
        _values.TryGetValue(
            name,
            out var value)
            ? value
            : throw new ArgumentException(
                $"--{name} is required.");
}
