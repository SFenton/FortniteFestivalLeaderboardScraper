using System.Globalization;
using System.Text.Json;
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
        var command = args[0];
        var options = CommandArguments.Parse(
            args.Skip(1));
        using var cancellation =
            new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        return command switch
        {
            "plan" => await PlanAsync(
                options,
                cancellation.Token),
            "quarantine" => await QuarantineAsync(
                options,
                cancellation.Token),
            "attest" => await AttestAsync(
                options,
                cancellation.Token),
            "reattach" => await ReattachAsync(
                options,
                cancellation.Token),
            _ => throw new ArgumentException(
                $"Unknown command: {command}"),
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

static async Task<int> PlanAsync(
    CommandArguments options,
    CancellationToken ct)
{
    var paths = QuarantineEvidencePaths.FromEnvironment();
    var package = paths.ResolveInputDirectory(
        options.Require("archive-package"));
    var proof = paths.ResolveInputFile(
        options.Require("proof-manifest"));
    var sourceManifest = paths.ResolveInputFile(
        options.Require("source-evidence-manifest"));
    var baseline = paths.ResolveInputFile(
        options.Require("baseline-route-manifest"));
    var candidate = paths.ResolveInputFile(
        options.Require("candidate-route-manifest"));
    var output = paths.ResolveNewOutputFile(
        options.Require("output"));

    var archive =
        QuarantineEvidenceValidator.ValidateArchivePackage(
            package,
            proof);
    var source =
        QuarantineEvidenceValidator.ValidateSourceEvidence(
            sourceManifest);
    var parity =
        QuarantineEvidenceValidator.ValidateRouteParity(
            baseline,
            candidate);
    ValidateEvidenceAlignment(archive, source, parity);

    await using var database =
        QuarantineDatabase.FromEnvironment(
            options.Get(
                "connection-env",
                QuarantineDatabase
                    .DefaultConnectionEnvironment),
            options.GetInt(
                "statement-timeout-seconds",
                120));
    var snapshot = await database.ReadSnapshotAsync(
        archive,
        ct);
    QuarantineDatabase.ValidateSnapshot(
        snapshot,
        archive,
        source,
        parity);
    var fingerprint =
        await database.ComputeFingerprintAsync(
            archive,
            ct);
    if (!string.Equals(
            fingerprint.Sha256,
            archive.RowFingerprintSha256,
            StringComparison.Ordinal)
        || fingerprint.RowCount != archive.RowCount)
    {
        throw new InvalidDataException(
            "Current candidate fingerprint differs from the accepted archive.");
    }

    var plan = new SnapshotGenerationQuarantinePlan(
        SchemaVersion: 1,
        ToolId:
            FSTService.Persistence.Maintenance
                .SnapshotGenerationQuarantineContract.ToolId,
        GeneratedAtUtc: DateTimeOffset.UtcNow,
        Archive: archive,
        SourceScrape: source,
        PreQuarantineParity: parity,
        Database: snapshot,
        ExplicitApprovalRequired: true,
        PlanDigest: null,
        OperationId: null).Seal();
    WriteNewCanonical(output, plan);
    Console.WriteLine(
        $"plan={output} digest={plan.PlanDigest} "
        + $"operation={plan.OperationId} "
        + $"target={archive.Instrument}/{archive.SnapshotId}");
    return 0;
}

static async Task<int> QuarantineAsync(
    CommandArguments options,
    CancellationToken ct)
{
    var paths = QuarantineEvidencePaths.FromEnvironment();
    var plan = ReadPlan(
        paths.ResolveInputFile(
            options.Require("plan")));
    RequireExpectedDigest(options, plan);
    var output = paths.ResolveNewOutputFile(
        options.Require("output"));
    RevalidatePlanEvidence(paths, plan);

    await using var database =
        QuarantineDatabase.FromEnvironment(
            options.Get(
                "connection-env",
                QuarantineDatabase
                    .DefaultConnectionEnvironment),
            options.GetInt(
                "statement-timeout-seconds",
                120));
    var current = await database.ReadSnapshotAsync(
        plan.Archive,
        ct);
    QuarantineDatabase.ValidateSnapshot(
        current,
        plan.Archive,
        plan.SourceScrape,
        plan.PreQuarantineParity);
    var report = await database.QuarantineAsync(
        plan,
        options.Require("approved-by"),
        options.Require("approval-reference"),
        ct);
    WriteNewCanonical(output, report);
    Console.WriteLine(
        $"quarantine={output} operation={report.OperationId} "
        + $"status={report.Status}");
    return 0;
}

static async Task<int> AttestAsync(
    CommandArguments options,
    CancellationToken ct)
{
    var paths = QuarantineEvidencePaths.FromEnvironment();
    var plan = ReadPlan(
        paths.ResolveInputFile(
            options.Require("plan")));
    RequireExpectedDigest(options, plan);
    var output = paths.ResolveNewOutputFile(
        options.Require("output"));
    RevalidatePlanEvidence(paths, plan);

    var parity =
        QuarantineEvidenceValidator.ValidateRouteParity(
            paths.ResolveInputFile(
                options.Require(
                    "baseline-route-manifest")),
            paths.ResolveInputFile(
                options.Require(
                    "candidate-route-manifest")));
    await using var database =
        QuarantineDatabase.FromEnvironment(
            options.Get(
                "connection-env",
                QuarantineDatabase
                    .DefaultConnectionEnvironment),
            options.GetInt(
                "statement-timeout-seconds",
                120));
    var report = await database.RecordAttestationAsync(
        plan,
        options.Require("stage"),
        options.Require("attested-by"),
        parity,
        ct);
    WriteNewCanonical(output, report);
    Console.WriteLine(
        $"attestation={output} operation={report.OperationId} "
        + $"stage={report.Stage} id={report.AttestationId}");
    return 0;
}

static async Task<int> ReattachAsync(
    CommandArguments options,
    CancellationToken ct)
{
    var paths = QuarantineEvidencePaths.FromEnvironment();
    var plan = ReadPlan(
        paths.ResolveInputFile(
            options.Require("plan")));
    RequireExpectedDigest(options, plan);
    var output = paths.ResolveNewOutputFile(
        options.Require("output"));
    RevalidatePlanEvidence(paths, plan);

    await using var database =
        QuarantineDatabase.FromEnvironment(
            options.Get(
                "connection-env",
                QuarantineDatabase
                    .DefaultConnectionEnvironment),
            options.GetInt(
                "statement-timeout-seconds",
                120));
    var report = await database.ReattachAsync(
        plan,
        options.Require("reattached-by"),
        options.Require("reattach-reference"),
        ct);
    WriteNewCanonical(output, report);
    Console.WriteLine(
        $"reattach={output} operation={report.OperationId} "
        + $"status={report.Status}");
    return 0;
}

static SnapshotGenerationQuarantinePlan ReadPlan(
    string path)
{
    var plan =
        JsonSerializer.Deserialize<
            SnapshotGenerationQuarantinePlan>(
            File.ReadAllBytes(path),
            QuarantineJson.Strict)
        ?? throw new InvalidDataException(
            $"Plan file is empty: {path}");
    plan.Validate();
    return plan;
}

static void RevalidatePlanEvidence(
    QuarantineEvidencePaths paths,
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
    ValidateEvidenceAlignment(archive, source, parity);
    if (archive != plan.Archive
        || source != plan.SourceScrape
        || parity != plan.PreQuarantineParity)
    {
        throw new InvalidDataException(
            "Current evidence differs from the sealed quarantine plan.");
    }
}

static void ValidateEvidenceAlignment(
    ArchivePackageEvidence archive,
    SourceScrapeEvidence source,
    RouteParityEvidence parity)
{
    if (archive.TriggerScrapeId !=
            source.PublishedScrapeId
        || archive.TriggerScrapeId !=
            parity.PublishedScrapeId
        || archive.TriggerPublicationId !=
            parity.PublicationId)
    {
        throw new InvalidDataException(
            "Archive, full-scrape, and route-parity evidence use different publications.");
    }
}

static void RequireExpectedDigest(
    CommandArguments options,
    SnapshotGenerationQuarantinePlan plan)
{
    var expected = options.Require(
        "expected-plan-digest");
    if (!string.Equals(
            expected,
            plan.PlanDigest,
            StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            "Expected plan digest differs from the sealed plan.");
    }
}

static void WriteNewCanonical<T>(
    string path,
    T value)
{
    var bytes = QuarantineJson.Canonical(value);
    using var stream = new FileStream(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        bufferSize: 16 * 1024,
        FileOptions.WriteThrough);
    stream.Write(bytes);
    stream.WriteByte((byte)'\n');
    stream.Flush(flushToDisk: true);
}

static void PrintUsage()
{
    Console.WriteLine(
        """
        Snapshot-generation quarantine executor

        Required environment:
          FST_SNAPSHOT_QUARANTINE_EVIDENCE_ROOT
          FST_SNAPSHOT_QUARANTINE_CONNECTION_STRING

        plan
          --archive-package <completed archive directory>
          --proof-manifest <accepted proof-manifest.json>
          --source-evidence-manifest <checksummed full-scrape manifest.json>
          --baseline-route-manifest <55-route baseline manifest.json>
          --candidate-route-manifest <55-route candidate manifest.json>
          --output <new plan.json>

        quarantine
          --plan <plan.json>
          --expected-plan-digest <sha256>
          --approved-by <operator>
          --approval-reference <approval evidence>
          --output <new quarantine-report.json>

        attest
          --plan <plan.json>
          --expected-plan-digest <sha256>
          --stage <quarantined|soak|reattached>
          --baseline-route-manifest <55-route baseline manifest.json>
          --candidate-route-manifest <55-route observed manifest.json>
          --attested-by <operator>
          --output <new attestation-report.json>

        reattach
          --plan <plan.json>
          --expected-plan-digest <sha256>
          --reattached-by <operator>
          --reattach-reference <rollback evidence>
          --output <new reattach-report.json>

        Optional for every command:
          --connection-env <environment variable name>
          --statement-timeout-seconds <5-600>

        There is no drop, truncate, delete, or automatic execution command.
        """);
}

public sealed class CommandArguments
{
    private readonly Dictionary<string, string> _values =
        new(StringComparer.Ordinal);

    public static CommandArguments Parse(
        IEnumerable<string> arguments)
    {
        var parsed = new CommandArguments();
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
            if (index + 1 >= tokens.Length
                || tokens[index + 1].StartsWith(
                    "--",
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Missing value for {token}.");
            }
            var key = token[2..];
            if (!parsed._values.TryAdd(
                    key,
                    tokens[++index]))
            {
                throw new ArgumentException(
                    $"Duplicate argument: --{key}");
            }
        }
        return parsed;
    }

    public string Require(string key) =>
        _values.TryGetValue(key, out var value)
        && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException(
                $"Missing --{key} <value>.");

    public string Get(
        string key,
        string fallback) =>
        _values.TryGetValue(key, out var value)
            ? value
            : fallback;

    public int GetInt(
        string key,
        int fallback)
    {
        if (!_values.TryGetValue(key, out var value))
            return fallback;
        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed)
            || parsed is < 5 or > 600)
        {
            throw new ArgumentException(
                $"--{key} must be between 5 and 600.");
        }
        return parsed;
    }
}
