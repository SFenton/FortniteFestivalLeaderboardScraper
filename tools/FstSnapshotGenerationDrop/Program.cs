using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using FstSnapshotGenerationDrop;
using FstSnapshotGenerationQuarantine;

return await MainAsync(args);

static async Task<int> MainAsync(string[] args)
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
            "select-canary" =>
                await SelectCanaryAsync(
                    DropCommandArguments.Parse(
                        args.Skip(1),
                        ["output"]),
                    cancellation.Token),
            "plan" =>
                await PlanAsync(
                    DropCommandArguments.Parse(
                        args.Skip(1),
                        [
                            "archive-package",
                            "proof-manifest",
                            "q1-plan",
                            "q1-quarantine-report",
                            "q1-quarantined-attestation",
                            "q1-soak-attestation",
                            "q1-reattach-report",
                            "q1-reattached-attestation",
                            "q2-plan",
                            "q2-quarantine-report",
                            "q2-quarantined-attestation",
                            "q2-soak-attestation",
                            "health-manifest",
                            "baseline-route-manifest",
                            "candidate-route-manifest",
                            "restore-image-id",
                            "capacity-reserve-bytes",
                            "recovery-bundle",
                            "output",
                        ]),
                    cancellation.Token),
            "drop" =>
                await DropAsync(
                    DropCommandArguments.Parse(
                        args.Skip(1),
                        [
                            "plan",
                            "expected-plan-digest",
                            "expected-operation-id",
                            "approved-by",
                            "approval-reference",
                            "output",
                        ]),
                    cancellation.Token),
            "confirm" =>
                await ConfirmAsync(
                    DropCommandArguments.Parse(
                        args.Skip(1),
                        [
                            "plan",
                            "expected-plan-digest",
                            "expected-operation-id",
                            "confirmed-by",
                            "confirmation-reference",
                            "output",
                        ]),
                    cancellation.Token),
            "attest" =>
                await AttestAsync(
                    DropCommandArguments.Parse(
                        args.Skip(1),
                        [
                            "plan",
                            "expected-plan-digest",
                            "stage",
                            "baseline-route-manifest",
                            "candidate-route-manifest",
                            "attested-by",
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

static async Task<int> SelectCanaryAsync(
    DropCommandArguments options,
    CancellationToken ct)
{
    var paths = DropEvidencePaths.FromEnvironment();
    var output = paths.ResolveNewFile(
        options.Require("output"));
    await using var database =
        DropDatabase.FromEnvironment();
    var candidate = await database.SelectCanaryAsync(ct);
    DropEvidenceValidator.WriteNewCanonical(
        output,
        candidate);
    Console.WriteLine(
        $"candidate={output} "
        + $"target={candidate.Instrument}/{candidate.SnapshotId} "
        + $"bytes={candidate.TotalBytes}");
    return 0;
}

static async Task<int> PlanAsync(
    DropCommandArguments options,
    CancellationToken ct)
{
    var paths = DropEvidencePaths.FromEnvironment();
    var archivePackage = paths.ResolveInputDirectory(
        options.Require("archive-package"));
    var proofManifest = paths.ResolveInputFile(
        options.Require("proof-manifest"));
    var q1PlanPath = paths.ResolveInputFile(
        options.Require("q1-plan"));
    var q1QuarantineReportPath = paths.ResolveInputFile(
        options.Require("q1-quarantine-report"));
    var q1QuarantinedPath = paths.ResolveInputFile(
        options.Require("q1-quarantined-attestation"));
    var q1SoakPath = paths.ResolveInputFile(
        options.Require("q1-soak-attestation"));
    var q1ReattachReportPath = paths.ResolveInputFile(
        options.Require("q1-reattach-report"));
    var q1ReattachedPath = paths.ResolveInputFile(
        options.Require("q1-reattached-attestation"));
    var q2PlanPath = paths.ResolveInputFile(
        options.Require("q2-plan"));
    var q2QuarantineReportPath = paths.ResolveInputFile(
        options.Require("q2-quarantine-report"));
    var q2QuarantinedPath = paths.ResolveInputFile(
        options.Require("q2-quarantined-attestation"));
    var q2SoakPath = paths.ResolveInputFile(
        options.Require("q2-soak-attestation"));
    var healthPath = paths.ResolveInputFile(
        options.Require("health-manifest"));
    var baselineRoute = paths.ResolveInputFile(
        options.Require("baseline-route-manifest"));
    var candidateRoute = paths.ResolveInputFile(
        options.Require("candidate-route-manifest"));
    var recoveryBundle = paths.ResolveNewDirectory(
        options.Require("recovery-bundle"));
    var output = paths.ResolveNewFile(
        options.Require("output"));

    var archive =
        QuarantineEvidenceValidator.ValidateArchivePackage(
            archivePackage,
            proofManifest);
    var q1Plan =
        DropEvidenceValidator.ReadQuarantinePlan(
            q1PlanPath);
    var q2Plan =
        DropEvidenceValidator.ReadQuarantinePlan(
            q2PlanPath);
    RevalidateQuarantinePlan(paths, q1Plan);
    RevalidateQuarantinePlan(paths, q2Plan);
    if (!SameArchiveTargetAndPackage(
            archive,
            q2Plan.Archive))
    {
        throw new InvalidDataException(
            "The supplied archive/proof does not match the active Q2 quarantine plan.");
    }
    var q1Quarantine =
        DropEvidenceValidator.ReadExecutionReport(
            q1QuarantineReportPath);
    var q1Quarantined =
        DropEvidenceValidator.ReadQuarantineAttestation(
            q1QuarantinedPath);
    var q1Soak =
        DropEvidenceValidator.ReadQuarantineAttestation(
            q1SoakPath);
    var q1Reattach =
        DropEvidenceValidator.ReadExecutionReport(
            q1ReattachReportPath);
    var q1Reattached =
        DropEvidenceValidator.ReadQuarantineAttestation(
            q1ReattachedPath);
    var q2Quarantine =
        DropEvidenceValidator.ReadExecutionReport(
            q2QuarantineReportPath);
    var q2Quarantined =
        DropEvidenceValidator.ReadQuarantineAttestation(
            q2QuarantinedPath);
    var q2Soak =
        DropEvidenceValidator.ReadQuarantineAttestation(
            q2SoakPath);
    foreach (var attestation in new[]
             {
                 q1Quarantined,
                 q1Soak,
                 q1Reattached,
                 q2Quarantined,
                 q2Soak,
             })
    {
        RevalidateAttestationPaths(paths, attestation);
    }
    DropEvidenceValidator.ValidateRehearsalEvidence(
        q1Plan,
        q1Quarantine,
        q1Quarantined,
        q1Soak,
        q1Reattach,
        q1Reattached);
    DropEvidenceValidator.ValidateQuarantineEvidence(
        q2Plan,
        q2Quarantine,
        q2Quarantined,
        q2Soak);
    DropEvidenceValidator.ValidateMatchingTargets(
        q1Plan,
        q2Plan);
    var q1Semantic =
        DropEvidenceValidator
            .ReadArchiveSemanticEvidence(
                q1Plan.Archive);
    var q2Semantic =
        DropEvidenceValidator
            .ReadArchiveSemanticEvidence(
                q2Plan.Archive);
    DropEvidenceValidator.ValidateMatchingSemantics(
        q1Semantic,
        q2Semantic);
    if (q2Soak.Parity.PublicationId !=
            q2Plan.Archive.TriggerPublicationId
        || q2Soak.Parity.PublishedScrapeId !=
            q2Plan.Archive.TriggerScrapeId)
    {
        throw new InvalidDataException(
            "Q2 soak advanced beyond its sealed cycle/publication.");
    }
    if (q1Reattach.CompletedAtUtc >=
            q2Quarantine.CompletedAtUtc)
    {
        throw new InvalidDataException(
            "Q1 reattach must complete before Q2 quarantine.");
    }
    if (q2Soak.CompletedAtUtc -
            q2Quarantine.CompletedAtUtc <
        TimeSpan.FromSeconds(
            SnapshotGenerationDropToolContract
                .MinimumSoakSeconds))
    {
        throw new InvalidDataException(
            "Q2 quarantine has not completed its 30-minute soak.");
    }

    var health =
        DropEvidenceValidator.ReadHealthEvidence(
            healthPath);
    var parity =
        QuarantineEvidenceValidator.ValidateRouteParity(
            baselineRoute,
            candidateRoute);
    if (health.PublicationId !=
            q2Plan.Archive.TriggerPublicationId
        || health.PublishedScrapeId !=
            q2Plan.Archive.TriggerScrapeId
        || parity.PublicationId !=
            q2Plan.Archive.TriggerPublicationId
        || parity.PublishedScrapeId !=
            q2Plan.Archive.TriggerScrapeId)
    {
        throw new InvalidDataException(
            "Q2 health and pre-drop parity must remain on its original publication.");
    }
    if (health.StartedAtUtc <
            q2Quarantine.CompletedAtUtc
        || q2Soak.CompletedAtUtc <
            health.CompletedAtUtc)
    {
        throw new InvalidDataException(
            "Q2 health must run after quarantine and complete before the soak attestation.");
    }
    if (DropEvidenceValidator.ReadRouteCapturedAt(
            baselineRoute) <
            q2Soak.CompletedAtUtc
        || DropEvidenceValidator.ReadRouteCapturedAt(
            candidateRoute) <
            q2Soak.CompletedAtUtc)
    {
        throw new InvalidDataException(
            "Pre-drop route captures are not fresh after the Q2 soak.");
    }
    var proofCompleted =
        DropEvidenceValidator.ReadProofCompletedAt(
            proofManifest);
    if (proofCompleted < health.CompletedAtUtc
        || proofCompleted <
            q2Soak.CompletedAtUtc)
    {
        throw new InvalidDataException(
            "The network-none proof is not fresh after Q2 soak.");
    }

    var binaryPath = Assembly
        .GetExecutingAssembly()
        .Location;
    var binarySha =
        DropEvidenceValidator.Sha256File(binaryPath);
    var restoreImageHash = NormalizeSha256(
        options.Require("restore-image-id"));
    var repositoryRoot = FindRepositoryRoot();
    var repositoryCommit = ReadRepositoryCommit(
        repositoryRoot);
    var restoreTool = Path.Combine(
        repositoryRoot,
        "tools",
        "postgres-snapshot-generation-restore.py");
    if (!File.Exists(restoreTool))
    {
        throw new FileNotFoundException(
            "The fixed restore tool was not found.",
            restoreTool);
    }
    var restoreToolSha =
        DropEvidenceValidator.Sha256File(restoreTool);
    var reserveBytes = options.GetInt64(
        "capacity-reserve-bytes");

    await using var database =
        DropDatabase.FromEnvironment();
    var snapshot = await database.ReadSnapshotAsync(
        q2Plan,
        ct: ct);
    var privateFingerprint =
        await database.ComputePrivateFingerprintAsync(
            q2Plan,
            ct: ct);
    if (privateFingerprint.RowCount !=
            q2Plan.Archive.RowCount
        || privateFingerprint.Sha256 !=
            q2Plan.Archive.RowFingerprintSha256)
    {
        throw new InvalidDataException(
            "Current Q2 private rows differ from the fresh archive.");
    }
    var provisional = new SnapshotGenerationDropPlan(
        1,
        SnapshotGenerationDropToolContract.ToolId,
        DateTimeOffset.UtcNow,
        true,
        q1Plan,
        q2Plan,
        q1Quarantine,
        q1Reattach,
        q2Quarantine,
        q1Quarantined,
        q1Soak,
        q1Reattached,
        q2Quarantined,
        q2Soak,
        q1Semantic,
        q2Semantic,
        parity,
        health,
        snapshot,
        recoveryBundle,
        new('0', 64),
        RequiredCapacity(
            archive.TotalBytes,
            new FileInfo(
                Path.Combine(
                    archivePackage,
                    "archive.custom")).Length),
        reserveBytes,
        binaryPath,
        binarySha,
        restoreTool,
        restoreToolSha,
        restoreImageHash,
        repositoryCommit,
        proofManifest,
        archive.ProofManifestSha256,
        proofCompleted,
        null,
        null);
    DropDatabase.ValidateBoundarySnapshot(
        provisional,
        snapshot);

    var evidenceFiles =
        new Dictionary<string, string>(
            StringComparer.Ordinal)
        {
            ["q1-plan"] = q1PlanPath,
            ["q1-proof"] =
                paths.ResolveInputFile(
                    q1Plan.Archive.ProofManifestPath),
            ["q1-source"] =
                paths.ResolveInputFile(
                    q1Plan.SourceScrape.ManifestPath),
            ["q1-pre-quarantine-baseline"] =
                paths.ResolveInputFile(
                    q1Plan.PreQuarantineParity
                        .BaselineManifestPath),
            ["q1-pre-quarantine-candidate"] =
                paths.ResolveInputFile(
                    q1Plan.PreQuarantineParity
                        .CandidateManifestPath),
            ["q1-quarantine-report"] =
                q1QuarantineReportPath,
            ["q1-quarantined-attestation"] =
                q1QuarantinedPath,
            ["q1-soak-attestation"] = q1SoakPath,
            ["q1-reattach-report"] =
                q1ReattachReportPath,
            ["q1-reattached-attestation"] =
                q1ReattachedPath,
            ["q2-plan"] = q2PlanPath,
            ["q2-proof"] =
                paths.ResolveInputFile(
                    q2Plan.Archive.ProofManifestPath),
            ["q2-source"] =
                paths.ResolveInputFile(
                    q2Plan.SourceScrape.ManifestPath),
            ["q2-pre-quarantine-baseline"] =
                paths.ResolveInputFile(
                    q2Plan.PreQuarantineParity
                        .BaselineManifestPath),
            ["q2-pre-quarantine-candidate"] =
                paths.ResolveInputFile(
                    q2Plan.PreQuarantineParity
                        .CandidateManifestPath),
            ["q2-quarantine-report"] =
                q2QuarantineReportPath,
            ["q2-quarantined-attestation"] =
                q2QuarantinedPath,
            ["q2-soak-attestation"] = q2SoakPath,
            ["health"] = healthPath,
            ["pre-drop-baseline"] = baselineRoute,
            ["pre-drop-candidate"] = candidateRoute,
            ["fresh-proof"] = proofManifest,
        };
    var bundleHash =
        DropEvidenceValidator.CreateRecoveryBundle(
            recoveryBundle,
            archivePackage,
            paths.ResolveInputDirectory(
                q1Plan.Archive.PackagePath),
            evidenceFiles,
            binaryPath,
            restoreTool,
            Path.Combine(
                repositoryRoot,
                "tools",
                "postgres-snapshot-generation-archive.py"),
            archive.TotalBytes,
            reserveBytes);
    var plan = (provisional with
    {
        RecoveryBundleManifestSha256 = bundleHash,
    }).Seal();
    await database.ValidateQuarantineChainAsync(
        plan,
        ct);
    DropEvidenceValidator.WriteNewCanonical(
        output,
        plan);
    Console.WriteLine(
        $"plan={output} digest={plan.PlanDigest} "
        + $"operation={plan.DropOperationId} "
        + $"target={archive.Instrument}/{archive.SnapshotId}");
    return 0;
}

static async Task<int> DropAsync(
    DropCommandArguments options,
    CancellationToken ct)
{
    var paths = DropEvidencePaths.FromEnvironment();
    var plan = ReadPlan(
        paths.ResolveInputFile(
            options.Require("plan")));
    ValidateExpectedIdentity(options, plan);
    if (DateTimeOffset.UtcNow - plan.GeneratedAtUtc >
            TimeSpan.FromMinutes(10)
        || plan.GeneratedAtUtc >
            DateTimeOffset.UtcNow.AddMinutes(1))
    {
        throw new InvalidDataException(
            "Drop plan is stale or has a future timestamp.");
    }
    ValidateCurrentBinary(plan);
    ValidateRecoveryBundle(paths, plan);
    RevalidatePlanArtifacts(paths, plan);
    var output = paths.ResolveNewFile(
        options.Require("output"));
    var approvalReference =
        options.Require("approval-reference");
    if (new[]
        {
            plan.RehearsalQuarantineReport.Reference,
            plan.RehearsalReattachReport.Reference,
            plan.ActiveQuarantineReport.Reference,
            plan.RehearsalPlan.OperationId!,
            plan.ActivePlan.OperationId!,
            plan.RehearsalPlan.PlanDigest!,
            plan.ActivePlan.PlanDigest!,
        }.Contains(
            approvalReference,
            StringComparer.Ordinal))
    {
        throw new InvalidDataException(
            "DROP approval must be distinct from Q1 and Q2 evidence references.");
    }

    await using var database =
        DropDatabase.FromEnvironment();
    await database.ValidateQuarantineChainAsync(
        plan,
        ct);
    var report = await database.DropAsync(
        plan,
        options.Require("approved-by"),
        approvalReference,
        ct);
    DropEvidenceValidator.WriteNewCanonical(
        output,
        report);
    Console.WriteLine(
        $"drop={output} operation={report.DropOperationId} "
        + $"status={report.Status} "
        + $"commit={report.CommitOutcome}");
    return 0;
}

static async Task<int> ConfirmAsync(
    DropCommandArguments options,
    CancellationToken ct)
{
    var paths = DropEvidencePaths.FromEnvironment();
    var plan = ReadPlan(
        paths.ResolveInputFile(
            options.Require("plan")));
    ValidateExpectedIdentity(options, plan);
    ValidateCurrentBinary(plan);
    ValidateRecoveryBundle(paths, plan);
    var output = paths.ResolveNewFile(
        options.Require("output"));
    await using var database =
        DropDatabase.FromEnvironment();
    var state = await database.ReadDropStateAsync(
        plan,
        ct);
    if (!state.OperationExists
        || state.OriginalRelationExists
        || state.QuarantineRelationExists
        || state.OriginalOidExists
        || !state.DurableDefaultExclusionPresent
        || !state.HoldActive)
    {
        throw new InvalidDataException(
            "Drop is not in an exact committed state.");
    }
    var report = new SnapshotGenerationDropExecutionReport(
        1,
        SnapshotGenerationDropToolContract.ToolId,
        "confirm",
        plan.DropOperationId!,
        plan.PlanDigest!,
        "dropped",
        "confirmed",
        DateTimeOffset.UtcNow,
        options.Require("confirmed-by"),
        options.Require("confirmation-reference"),
        plan.ActivePlan.Archive.Instrument,
        plan.ActivePlan.Archive.SnapshotId,
        plan.ActivePlan.Archive.ChildOid,
        plan.ActivePlan.Archive.ChildRelfilenode,
        plan.ActivePlan.Archive.RowCount,
        plan.ActivePlan.Archive.RowFingerprintSha256,
        JsonSerializer.SerializeToElement(
            state,
            DropJson.Strict)).Seal();
    DropEvidenceValidator.WriteNewCanonical(
        output,
        report);
    Console.WriteLine(
        $"confirmation={output} "
        + $"operation={plan.DropOperationId}");
    return 0;
}

static async Task<int> AttestAsync(
    DropCommandArguments options,
    CancellationToken ct)
{
    var paths = DropEvidencePaths.FromEnvironment();
    var plan = ReadPlan(
        paths.ResolveInputFile(
            options.Require("plan")));
    RequireExpectedDigest(options, plan);
    ValidateCurrentBinary(plan);
    var stage = options.Require("stage");
    if (stage is not (
            "pre_drop"
            or "dropped"
            or "post_publication"))
    {
        throw new ArgumentException(
            "--stage must be pre_drop, dropped, or post_publication.");
    }
    var parity =
        QuarantineEvidenceValidator.ValidateRouteParity(
            paths.ResolveInputFile(
                options.Require(
                    "baseline-route-manifest")),
            paths.ResolveInputFile(
                options.Require(
                    "candidate-route-manifest")));
    var output = paths.ResolveNewFile(
        options.Require("output"));
    SnapshotGenerationDropAttestationReport report;
    await using var database =
        DropDatabase.FromEnvironment();
    if (stage == "pre_drop")
    {
        if (parity != plan.PreDropParity)
        {
            throw new InvalidDataException(
                "Pre-drop attestation differs from the parity sealed in the plan.");
        }
        var snapshot = await database.ReadSnapshotAsync(
            plan.ActivePlan,
            ct: ct);
        DropDatabase.ValidateBoundarySnapshot(
            plan,
            snapshot);
        if (parity.PublicationId !=
                snapshot.CurrentPublicationId
            || parity.PublishedScrapeId !=
                snapshot.PublishedScrapeId)
        {
            throw new InvalidDataException(
                "Pre-drop route parity differs from the current publication.");
        }
        var databaseEvidence =
            JsonSerializer.SerializeToElement(
                snapshot,
                DropJson.Strict);
        var evidenceHash = DropJson.Sha256(
            new
            {
                Stage = stage,
                Parity = parity,
                Database = snapshot,
            });
        report = new SnapshotGenerationDropAttestationReport(
            1,
            SnapshotGenerationDropToolContract.ToolId,
            plan.DropOperationId!,
            stage,
            null,
            DateTimeOffset.UtcNow,
            options.Require("attested-by"),
            parity,
            databaseEvidence,
            evidenceHash).Seal();
    }
    else
    {
        report = await database.RecordAttestationAsync(
            plan,
            stage,
            options.Require("attested-by"),
            parity,
            ct);
    }
    DropEvidenceValidator.WriteNewCanonical(
        output,
        report);
    Console.WriteLine(
        $"attestation={output} operation={plan.DropOperationId} "
        + $"stage={stage}");
    return 0;
}

static SnapshotGenerationDropPlan ReadPlan(string path)
{
    var plan =
        DropEvidenceValidator.ReadStrict<
            SnapshotGenerationDropPlan>(path);
    plan.Validate();
    return plan;
}

static void RevalidatePlanArtifacts(
    DropEvidencePaths paths,
    SnapshotGenerationDropPlan plan)
{
    RevalidateQuarantinePlan(
        paths,
        plan.RehearsalPlan);
    RevalidateQuarantinePlan(
        paths,
        plan.ActivePlan);
    var freshArchive =
        QuarantineEvidenceValidator.ValidateArchivePackage(
            paths.ResolveInputDirectory(
                plan.ActivePlan.Archive.PackagePath),
            paths.ResolveInputFile(
                plan.FreshProofManifestPath));
    if (!SameArchiveTargetAndPackage(
            freshArchive,
            plan.ActivePlan.Archive)
        || freshArchive.ProofManifestSha256 !=
            plan.FreshProofManifestSha256)
    {
        throw new InvalidDataException(
            "Fresh network-none proof differs from the sealed plan.");
    }
    var rehearsalSemantic =
        DropEvidenceValidator
            .ReadArchiveSemanticEvidence(
                plan.RehearsalPlan.Archive);
    var activeSemantic =
        DropEvidenceValidator
            .ReadArchiveSemanticEvidence(
                plan.ActivePlan.Archive);
    if (DropJson.Sha256(rehearsalSemantic) !=
            DropJson.Sha256(
                plan.RehearsalSemantic)
        || DropJson.Sha256(activeSemantic) !=
            DropJson.Sha256(
                plan.ActiveSemantic))
    {
        throw new InvalidDataException(
            "Archive semantic evidence differs from the sealed plan.");
    }
    DropEvidenceValidator.ValidateMatchingSemantics(
        rehearsalSemantic,
        activeSemantic);
    var parity =
        QuarantineEvidenceValidator.ValidateRouteParity(
            paths.ResolveInputFile(
                plan.PreDropParity.BaselineManifestPath),
            paths.ResolveInputFile(
                plan.PreDropParity.CandidateManifestPath));
    if (parity != plan.PreDropParity)
    {
        throw new InvalidDataException(
            "Pre-drop route evidence changed.");
    }
    foreach (var attestation in new[]
             {
                 plan.RehearsalQuarantinedAttestation,
                 plan.RehearsalSoakAttestation,
                 plan.RehearsalReattachedAttestation,
                 plan.ActiveQuarantinedAttestation,
                 plan.ActiveSoakAttestation,
             })
    {
        RevalidateAttestationPaths(paths, attestation);
    }
}

static void RevalidateQuarantinePlan(
    DropEvidencePaths paths,
    SnapshotGenerationQuarantinePlan plan)
{
    var archive =
        QuarantineEvidenceValidator.ValidateArchivePackage(
            paths.ResolveInputDirectory(
                plan.Archive.PackagePath),
            paths.ResolveInputFile(
                plan.Archive.ProofManifestPath));
    var source =
        QuarantineEvidenceValidator.ValidateSourceEvidence(
            paths.ResolveInputFile(
                plan.SourceScrape.ManifestPath));
    var parity =
        QuarantineEvidenceValidator.ValidateRouteParity(
            paths.ResolveInputFile(
                plan.PreQuarantineParity
                    .BaselineManifestPath),
            paths.ResolveInputFile(
                plan.PreQuarantineParity
                    .CandidateManifestPath));
    if (archive != plan.Archive
        || source != plan.SourceScrape
        || parity != plan.PreQuarantineParity)
    {
        throw new InvalidDataException(
            "Current evidence differs from a sealed quarantine plan.");
    }
}

static void RevalidateAttestationPaths(
    DropEvidencePaths paths,
    SnapshotGenerationQuarantineAttestationReport
        attestation)
{
    var parity =
        QuarantineEvidenceValidator.ValidateRouteParity(
            paths.ResolveInputFile(
                attestation.Parity.BaselineManifestPath),
            paths.ResolveInputFile(
                attestation.Parity.CandidateManifestPath));
    if (parity != attestation.Parity)
    {
        throw new InvalidDataException(
            $"Route evidence changed for {attestation.Stage} attestation.");
    }
}

static void ValidateExpectedIdentity(
    DropCommandArguments options,
    SnapshotGenerationDropPlan plan)
{
    RequireExpectedDigest(options, plan);
    if (options.Require("expected-operation-id") !=
            plan.DropOperationId)
    {
        throw new InvalidDataException(
            "Expected drop identity differs from the sealed plan.");
    }
}

static void RequireExpectedDigest(
    DropCommandArguments options,
    SnapshotGenerationDropPlan plan)
{
    if (options.Require("expected-plan-digest") !=
        plan.PlanDigest)
    {
        throw new InvalidDataException(
            "Expected plan digest differs from the sealed plan.");
    }
}

static void ValidateCurrentBinary(
    SnapshotGenerationDropPlan plan)
{
    var current = DropEvidenceValidator.Sha256File(
        Assembly.GetExecutingAssembly().Location);
    if (current != plan.BinarySha256)
    {
        throw new InvalidDataException(
            "Executing drop binary differs from the sealed plan.");
    }
    if (ReadRepositoryCommit(
            FindRepositoryRoot()) !=
        plan.RepositoryCommit)
    {
        throw new InvalidDataException(
            "Repository commit differs from the sealed plan.");
    }
}

static void ValidateRecoveryBundle(
    DropEvidencePaths paths,
    SnapshotGenerationDropPlan plan)
{
    var directory = paths.ResolveInputDirectory(
        plan.RecoveryBundlePath);
    var manifest =
        DropEvidenceValidator.ValidateRecoveryBundle(
        directory);
    var actual = DropEvidenceValidator.Sha256File(
        Path.Combine(
            directory,
            "bundle-manifest.json"));
    if (actual != plan.RecoveryBundleManifestSha256)
    {
        throw new InvalidDataException(
            "Recovery bundle differs from the sealed plan.");
    }
    if (manifest.RequiredCapacityBytes !=
            plan.RequiredCapacityBytes
        || manifest.CapacityReserveBytes !=
            plan.CapacityReserveBytes
        || manifest.PhysicalBytes !=
            plan.ActivePlan.Archive.TotalBytes)
    {
        throw new InvalidDataException(
            "Recovery capacity evidence differs from the sealed plan.");
    }
    var restoreTool = manifest.Files.SingleOrDefault(
        static file =>
            file.Path == "restore-tool.py");
    if (restoreTool is null
        || restoreTool.Sha256 != plan.RestoreToolSha256)
    {
        throw new InvalidDataException(
            "Pinned restore tool differs from the sealed plan.");
    }
    if (!manifest.Files.Any(
            static file =>
                file.Path ==
                    "postgres-snapshot-generation-archive.py"))
    {
        throw new InvalidDataException(
            "Pinned archive helper is missing.");
    }
    var dropBinary = manifest.Files.SingleOrDefault(
        static file =>
            file.Path == "drop-binary");
    if (dropBinary is null
        || dropBinary.Sha256 != plan.BinarySha256)
    {
        throw new InvalidDataException(
            "Pinned drop binary differs from the sealed plan.");
    }
}

static string ReadRepositoryCommit(string root)
{
    var start = new ProcessStartInfo(
        "git",
        $"-C \"{root}\" rev-parse HEAD")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    using var process = Process.Start(start)
        ?? throw new InvalidOperationException(
            "Could not start git.");
    var output = process.StandardOutput.ReadToEnd()
        .Trim();
    process.WaitForExit();
    if (process.ExitCode != 0
        || output.Length != 40
        || output.Any(character =>
            character is not (
                >= '0' and <= '9'
                or >= 'a' and <= 'f')))
    {
        throw new InvalidDataException(
            "Repository commit identity is unavailable.");
    }
    return output;
}

static string FindRepositoryRoot()
{
    var current = new DirectoryInfo(
        AppContext.BaseDirectory);
    while (current is not null)
    {
        if (File.Exists(
                Path.Combine(
                    current.FullName,
                    "FortniteFestivalLeaderboardScraper.sln")))
        {
            return current.FullName;
        }
        current = current.Parent;
    }
    throw new DirectoryNotFoundException(
        "Repository root was not found.");
}

static string NormalizeSha256(string value)
{
    var normalized = value.StartsWith(
        "sha256:",
        StringComparison.Ordinal)
        ? value[7..]
        : value;
    if (normalized.Length != 64
        || normalized.Any(character =>
            character is not (
                >= '0' and <= '9'
                or >= 'a' and <= 'f')))
    {
        throw new ArgumentException(
            "Restore image ID must be a lowercase SHA-256.");
    }
    return normalized;
}

static bool SameArchiveTargetAndPackage(
    ArchivePackageEvidence left,
    ArchivePackageEvidence right) =>
    left.PackagePath == right.PackagePath
    && left.PackageManifestSha256 ==
        right.PackageManifestSha256
    && left.ArchiveSha256 == right.ArchiveSha256
    && left.CycleId == right.CycleId
    && left.TriggerScrapeId == right.TriggerScrapeId
    && left.TriggerPublicationId ==
        right.TriggerPublicationId
    && left.ObservationId == right.ObservationId
    && left.Instrument == right.Instrument
    && left.SnapshotId == right.SnapshotId
    && left.RootOid == right.RootOid
    && left.ChildOid == right.ChildOid
    && left.ChildRelfilenode ==
        right.ChildRelfilenode
    && left.RowCount == right.RowCount
    && left.RowFingerprintSha256 ==
        right.RowFingerprintSha256
    && left.LogicalCatalogSha256 ==
        right.LogicalCatalogSha256;

static long RequiredCapacity(
    long physicalBytes,
    long archiveBytes) =>
    Math.Max(
        checked(
            2 * physicalBytes
            + archiveBytes
            + 1024L * 1024 * 1024),
        2L * 1024 * 1024 * 1024);

static void PrintUsage()
{
    Console.WriteLine(
        """
        Snapshot-generation DROP-only executor

        Required environment:
          FST_SNAPSHOT_DROP_EVIDENCE_ROOT
          FST_SNAPSHOT_DROP_CONNECTION_STRING
          FST_SNAPSHOT_DROP_BINARY_SHA256 (wrapper)

        Commands:
          select-canary --output <new candidate.json>
          plan
            --archive-package <fresh Q2 archive directory>
            --proof-manifest <fresh post-Q2 proof-manifest.json>
            --q1-plan <plan.json>
            --q1-quarantine-report <report.json>
            --q1-quarantined-attestation <attestation.json>
            --q1-soak-attestation <attestation.json>
            --q1-reattach-report <report.json>
            --q1-reattached-attestation <attestation.json>
            --q2-plan <plan.json>
            --q2-quarantine-report <report.json>
            --q2-quarantined-attestation <attestation.json>
            --q2-soak-attestation <attestation.json>
            --health-manifest <60-sample health.json>
            --baseline-route-manifest <manifest.json>
            --candidate-route-manifest <manifest.json>
            --restore-image-id <sha256>
            --capacity-reserve-bytes <bytes>
            --recovery-bundle <new directory>
            --output <new plan.json>
          drop --plan <plan.json> --expected-plan-digest <sha256>
               --expected-operation-id <id>
               --approved-by <operator>
               --approval-reference <distinct approval>
               --output <new report.json>
          confirm --plan <plan.json> --expected-plan-digest <sha256>
               --expected-operation-id <id>
               --confirmed-by <operator>
               --confirmation-reference <reference>
               --output <new report.json>
          attest --plan <plan.json> --expected-plan-digest <sha256>
               --stage <pre_drop|dropped|post_publication>
               --baseline-route-manifest <manifest.json>
               --candidate-route-manifest <manifest.json>
               --attested-by <operator>
               --output <new report.json>

        There is no arbitrary relation, schema, table, SQL, batch, force,
        automatic, delete, truncate, or cascading command.
        """);
}

public sealed class DropCommandArguments
{
    private readonly Dictionary<string, string> _values =
        new(StringComparer.Ordinal);

    public static DropCommandArguments Parse(
        IEnumerable<string> arguments,
        IReadOnlyCollection<string> allowed)
    {
        var result = new DropCommandArguments();
        var allowedSet = allowed.ToHashSet(
            StringComparer.Ordinal);
        var tokens = arguments.ToArray();
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (!token.StartsWith(
                    "--",
                    StringComparison.Ordinal)
                || token.Length == 2)
            {
                throw new ArgumentException(
                    $"Unexpected argument: {token}");
            }
            var key = token[2..];
            if (!allowedSet.Contains(key))
            {
                throw new ArgumentException(
                    $"Unsupported argument: {token}");
            }
            if (index + 1 >= tokens.Length
                || tokens[index + 1].StartsWith(
                    "--",
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Missing value for {token}.");
            }
            if (!result._values.TryAdd(
                    key,
                    tokens[++index]))
            {
                throw new ArgumentException(
                    $"Duplicate argument: --{key}");
            }
        }
        return result;
    }

    public string Require(string key) =>
        _values.TryGetValue(key, out var value)
        && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException(
                $"Missing --{key} <value>.");

    public long GetInt64(string key)
    {
        var value = Require(key);
        if (!long.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed)
            || parsed < 0)
        {
            throw new ArgumentException(
                $"--{key} must be a non-negative integer.");
        }
        return parsed;
    }
}
