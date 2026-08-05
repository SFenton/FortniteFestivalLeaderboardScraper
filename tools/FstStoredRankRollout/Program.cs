using System.Globalization;
using System.Text;
using System.Text.Json;
using FstStoredRankRollout;

return await MainAsync(args);

static async Task<int> MainAsync(string[] args)
{
    if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
    {
        PrintUsage();
        return 0;
    }

    try
    {
        var command = args[0];
        var options = CommandArguments.Parse(args.Skip(1));
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        return command switch
        {
            "self-test" => RunSelfTest(),
            "manifest" => await GenerateManifestAsync(options, cancellation.Token),
            "preflight" => await RunPreflightAsync(options, cancellation.Token),
            "guard" => await RunManifestGuardAsync(options, cancellation.Token),
            "db-attest" => await RunDatabaseAttestationAsync(options, cancellation.Token),
            "row-parity" => await RunRowParityAsync(options, cancellation.Token),
            "api-capture" => await CaptureApiAsync(options, cancellation.Token),
            "api-compare" => await CompareApiAsync(options, cancellation.Token),
            "schedule" => await WriteScheduleAsync(options, cancellation.Token),
            "benchmark-block" => await RunBenchmarkBlockAsync(options, cancellation.Token),
            "analyze" => await AnalyzeAsync(options, cancellation.Token),
            "finalize-acceptance" => await FinalizeAcceptanceAsync(options, cancellation.Token),
            "validate-manifest" => await ValidateManifestAsync(options, cancellation.Token),
            _ => throw new ArgumentException($"Unknown command: {command}"),
        };
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Cancelled.");
        return 130;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
}

static async Task<bool> ValidateRolloutRoleAsync(
    Npgsql.NpgsqlDataSource dataSource,
    CancellationToken cancellationToken)
{
    await using var visibilityProbe =
        ReadOnlyPostgres.CreateVisibilityProbeDataSource();
    return await ReadOnlyPostgres.ValidateSelectTempOnlyRoleAsync(
        dataSource,
        visibilityProbe,
        cancellationToken);
}

static async Task<int> GenerateManifestAsync(
    CommandArguments options,
    CancellationToken cancellationToken)
{
    var output = EvidencePaths.ResolveOutput(options.Require("output"));
    var seed = options.GetInt("seed", 20260804);
    await using var dataSource = ReadOnlyPostgres.CreateDataSource(
        options.Get("connection-env", ReadOnlyPostgres.DefaultConnectionEnvironment),
        options.GetInt("statement-timeout-seconds", 30));
    await ValidateRolloutRoleAsync(dataSource, cancellationToken);
    var generator = new ManifestGenerator(dataSource);
    var postgresNetworkBindings =
        JsonSerializer.Deserialize<IReadOnlyList<PostgresNetworkBinding>>(
            options.Require("postgres-network-bindings-json"),
            RolloutJson.Options)
        ?? throw new InvalidDataException(
            "--postgres-network-bindings-json did not contain an array.");
    var manifest = await generator.GenerateAsync(
        seed,
        options.GetInt("max-mapped-scopes", 20_000),
        options.GetInt("max-tie-scopes-per-instrument", 12),
        options.Require("service-image"),
        options.Require("service-image-id"),
        options.Require("worker-container-id"),
        options.Require("worker-image"),
        options.Require("worker-image-id"),
        options.Require("worker-container-status"),
        options.Require("worker-container-state"),
        options.Require("service-db-host"),
        options.GetInt("service-db-port", 0),
        options.Require("service-db-name"),
        options.Require("service-db-username"),
        options.Require("postgres-container-id"),
        options.Require("postgres-image"),
        options.Require("postgres-image-id"),
        options.Require("postgres-network-names")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        options.Require("postgres-network-aliases")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        options.Require("postgres-server-addresses")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        postgresNetworkBindings,
        options.Require("evidence-mount-target"),
        options.Require("evidence-mount-source"),
        options.Require("evidence-mount-filesystem"),
        cancellationToken);
    await JsonFiles.WriteAsync(output, manifest, cancellationToken);
    Console.WriteLine(
        $"manifest={output} fingerprint={manifest.SelectionFingerprint} " +
        $"promotionReady={manifest.Coverage.PromotionReady}");
    if (!manifest.Coverage.PromotionReady)
        Console.WriteLine($"missing={string.Join(',', manifest.Coverage.MissingRequirements)}");
    return manifest.Coverage.PromotionReady ? 0 : 2;
}

static async Task<int> RunPreflightAsync(
    CommandArguments options,
    CancellationToken cancellationToken)
{
    var output = EvidencePaths.ResolveOutput(options.Require("output"));
    var expected = options.GetLong("expected-published-scrape", 0);
    if (expected <= 0)
        throw new ArgumentException("--expected-published-scrape must be positive.");
    await using var dataSource = ReadOnlyPostgres.CreateDataSource(
        options.Get("connection-env", ReadOnlyPostgres.DefaultConnectionEnvironment),
        options.GetInt("statement-timeout-seconds", 15));
    var crossRoleVisibilityAttested =
        await ValidateRolloutRoleAsync(dataSource, cancellationToken);
    var manifestPath = options.Get("manifest", "");
    var report = string.IsNullOrWhiteSpace(manifestPath)
        ? await ReadOnlyPostgres.ReadPreflightAsync(
            dataSource,
            expected,
            cancellationToken)
        : await ReadOnlyPostgres.ReadPreflightAsync(
            dataSource,
            expected,
            await ReadManifestAsync(
                EvidencePaths.ResolveInput(manifestPath),
                cancellationToken),
            cancellationToken);
    report.CrossRoleVisibilityAttested = crossRoleVisibilityAttested;
    await JsonFiles.WriteAsync(output, report, cancellationToken);
    Console.WriteLine($"preflight={output} passed={report.Passed}");
    return report.Passed ? 0 : 3;
}

static async Task<int> RunRowParityAsync(
    CommandArguments options,
    CancellationToken cancellationToken)
{
    var manifestPath = EvidencePaths.ResolveInput(options.Require("manifest"));
    var output = EvidencePaths.ResolveOutput(options.Require("output"));
    var manifest = await ReadManifestAsync(manifestPath, cancellationToken);
    await using var dataSource = ReadOnlyPostgres.CreateDataSource(
        options.Get("connection-env", ReadOnlyPostgres.DefaultConnectionEnvironment),
        options.GetInt("statement-timeout-seconds", 30));
    await ValidateRolloutRoleAsync(dataSource, cancellationToken);
    var report = await new ParityRunner(dataSource).RunAsync(manifest, cancellationToken);
    await JsonFiles.WriteAsync(output, report, cancellationToken);
    Console.WriteLine(
        $"rowParity={output} cases={report.CaseCount} " +
        $"differences={report.DifferenceCount} passed={report.Passed}");
    return report.Passed ? 0 : 4;
}

static async Task<int> RunManifestGuardAsync(
    CommandArguments options,
    CancellationToken cancellationToken)
{
    var manifest = await ReadManifestAsync(
        EvidencePaths.ResolveInput(options.Require("manifest")),
        cancellationToken);
    var output = EvidencePaths.ResolveOutput(options.Require("output"));
    await using var dataSource = ReadOnlyPostgres.CreateDataSource(
        options.Get("connection-env", ReadOnlyPostgres.DefaultConnectionEnvironment),
        options.GetInt("statement-timeout-seconds", 30));
    await ValidateRolloutRoleAsync(dataSource, cancellationToken);
    var report = await new ManifestGenerator(dataSource).ValidateGuardAsync(
        manifest,
        cancellationToken);
    await JsonFiles.WriteAsync(output, report, cancellationToken);
    Console.WriteLine($"manifestGuard={output} passed={report.Passed}");
    return report.Passed ? 0 : 8;
}

static async Task<int> CaptureApiAsync(
    CommandArguments options,
    CancellationToken cancellationToken)
{
    var manifestPath = EvidencePaths.ResolveInput(options.Require("manifest"));
    var outputDirectory = EvidencePaths.ResolveOutput(options.Require("output-dir"));
    var manifest = await ReadManifestAsync(manifestPath, cancellationToken);
    var variant = options.Require("variant");
    var baseUri = new Uri(options.Require("base-url"), UriKind.Absolute);
    var report = await new ApiRunner().CaptureAsync(
        manifest,
        baseUri,
        variant,
        outputDirectory,
        cancellationToken);
    var reportPath = Path.Combine(outputDirectory, "capture.json");
    await JsonFiles.WriteAsync(reportPath, report, cancellationToken);
    Console.WriteLine(
        $"apiCapture={reportPath} variant={variant} workloads={report.Items.Count} " +
        $"unexpectedStatuses={report.UnexpectedStatusCount} passed={report.Passed}");
    return report.Passed ? 0 : 9;
}

static async Task<int> RunDatabaseAttestationAsync(
    CommandArguments options,
    CancellationToken cancellationToken)
{
    var manifest = await ReadManifestAsync(
        EvidencePaths.ResolveInput(options.Require("manifest")),
        cancellationToken);
    await using var dataSource = ReadOnlyPostgres.CreateDataSource(
        options.Get("connection-env", ReadOnlyPostgres.DefaultConnectionEnvironment),
        options.GetInt("statement-timeout-seconds", 15));
    await ValidateRolloutRoleAsync(dataSource, cancellationToken);
    var observed = await ReadOnlyPostgres.ReadDatabaseIdentityAsync(
        dataSource,
        cancellationToken);
    var report = ReadOnlyPostgres.CompareDatabaseIdentity(manifest, observed);
    var configuredOutput = options.Get("output", "");
    if (string.IsNullOrWhiteSpace(configuredOutput))
    {
        Console.WriteLine($"databaseAttestation=in-memory passed={report.Passed}");
    }
    else
    {
        var output = EvidencePaths.ResolveOutput(configuredOutput);
        await JsonFiles.WriteAsync(output, report, cancellationToken);
        Console.WriteLine($"databaseAttestation={output} passed={report.Passed}");
    }
    return report.Passed ? 0 : 11;
}

static async Task<int> CompareApiAsync(
    CommandArguments options,
    CancellationToken cancellationToken)
{
    var baselinePath = EvidencePaths.ResolveInput(options.Require("baseline-report"));
    var candidatePath = EvidencePaths.ResolveInput(options.Require("candidate-report"));
    var output = EvidencePaths.ResolveOutput(options.Require("output"));
    var baseline = await JsonFiles.ReadAsync<ApiCaptureReport>(baselinePath, cancellationToken);
    var candidate = await JsonFiles.ReadAsync<ApiCaptureReport>(candidatePath, cancellationToken);
    var report = await ApiRunner.CompareAsync(
        baseline,
        Path.GetDirectoryName(baselinePath)!,
        candidate,
        Path.GetDirectoryName(candidatePath)!,
        cancellationToken);
    await JsonFiles.WriteAsync(output, report, cancellationToken);
    Console.WriteLine(
        $"apiComparison={output} differences={report.DifferenceCount} passed={report.Passed}");
    return report.Passed ? 0 : 5;
}

static async Task<int> WriteScheduleAsync(
    CommandArguments options,
    CancellationToken cancellationToken)
{
    var manifestPath = EvidencePaths.ResolveInput(options.Require("manifest"));
    var output = EvidencePaths.ResolveOutput(options.Require("output"));
    var manifest = await ReadManifestAsync(manifestPath, cancellationToken);
    var seed = options.GetInt("seed", manifest.Seed);
    var schedule = DeterministicRollout.BuildSchedule(manifest, seed);
    EvidencePaths.EnsureParentDirectory(output);
    if (string.Equals(Path.GetExtension(output), ".tsv", StringComparison.OrdinalIgnoreCase))
    {
        var builder = new StringBuilder();
        builder.AppendLine("sequence\tmode\tconcurrency\tworkloadId\tabbaBlock\tposition\tvariant\trequestCount");
        foreach (var entry in schedule)
        {
            builder.Append(entry.Sequence).Append('\t')
                .Append(entry.Mode).Append('\t')
                .Append(entry.Concurrency).Append('\t')
                .Append(entry.WorkloadId).Append('\t')
                .Append(entry.AbbaBlock).Append('\t')
                .Append(entry.Position).Append('\t')
                .Append(entry.Variant).Append('\t')
                .Append(entry.RequestCount).AppendLine();
        }
        await File.WriteAllTextAsync(output, builder.ToString(), cancellationToken);
    }
    else
    {
        await JsonFiles.WriteAsync(output, schedule, cancellationToken);
    }
    Console.WriteLine($"schedule={output} entries={schedule.Count}");
    return 0;
}

static async Task<int> RunBenchmarkBlockAsync(
    CommandArguments options,
    CancellationToken cancellationToken)
{
    var manifestPath = EvidencePaths.ResolveInput(options.Require("manifest"));
    var output = EvidencePaths.ResolveOutput(options.Require("output"));
    var manifest = await ReadManifestAsync(manifestPath, cancellationToken);
    var workloadId = options.Require("workload-id");
    var workload = manifest.ApiWorkloads.SingleOrDefault(item =>
                       string.Equals(item.Id, workloadId, StringComparison.Ordinal))
                   ?? throw new ArgumentException($"Unknown workload: {workloadId}");
    var schedule = new BenchmarkScheduleEntry
    {
        Sequence = options.GetInt("sequence", 0),
        Mode = options.Require("mode"),
        Concurrency = options.GetInt("concurrency", 1),
        WorkloadId = workloadId,
        Variant = options.Require("variant"),
        RequestCount = options.GetInt("request-count", 1),
    };
    if (schedule.Sequence <= 0
        || schedule.Concurrency is not (1 or 8)
        || schedule.RequestCount <= 0
        || schedule.Mode is not ("cold" or "warm")
        || schedule.Variant is not ("baseline" or "candidate"))
    {
        throw new ArgumentException("Invalid benchmark schedule arguments.");
    }

    await using var dataSource = ReadOnlyPostgres.CreateDataSource(
        options.Get("connection-env", ReadOnlyPostgres.DefaultConnectionEnvironment),
        options.GetInt("statement-timeout-seconds", 30),
        maxPoolSize: Math.Max(16, schedule.Concurrency * 2));
    await ValidateRolloutRoleAsync(dataSource, cancellationToken);
    var databaseAttestation = ReadOnlyPostgres.CompareDatabaseIdentity(
        manifest,
        await ReadOnlyPostgres.ReadDatabaseIdentityAsync(
            dataSource,
            cancellationToken));
    if (!databaseAttestation.Passed)
        throw new InvalidOperationException("Benchmark database identity drifted.");
    var report = await new ApiRunner().BenchmarkAsync(
        dataSource,
        workload,
        new Uri(options.Require("base-url"), UriKind.Absolute),
        schedule,
        options.Require("postgres-container"),
        options.GetInt(
            "warm-request-starts-per-second",
            ApiRunner.DefaultWarmRequestStartsPerSecond),
        cancellationToken);
    report.DatabaseAttestation = databaseAttestation;
    await JsonFiles.WriteAsync(output, report, cancellationToken);
    Console.WriteLine(
        $"benchmark={output} samples={report.CompletedCount} errors={report.ErrorCount}");
    return report.ErrorCount == 0 ? 0 : 6;
}

static async Task<int> AnalyzeAsync(
    CommandArguments options,
    CancellationToken cancellationToken)
{
    var manifest = await ReadManifestAsync(
        EvidencePaths.ResolveInput(options.Require("manifest")),
        cancellationToken);
    var parity = await JsonFiles.ReadAsync<ParityReport>(
        EvidencePaths.ResolveInput(options.Require("row-parity")),
        cancellationToken);
    var apiComparison = await JsonFiles.ReadAsync<ApiComparisonReport>(
        EvidencePaths.ResolveInput(options.Require("api-comparison")),
        cancellationToken);
    var blocksDirectory = EvidencePaths.ResolveOutput(options.Require("blocks-dir"));
    if (!Directory.Exists(blocksDirectory))
        throw new DirectoryNotFoundException(blocksDirectory);
    var blocks = new List<BenchmarkBlockReport>();
    foreach (var path in Directory.EnumerateFiles(
                 blocksDirectory,
                 "block-*.json",
                 SearchOption.AllDirectories))
    {
        blocks.Add(await JsonFiles.ReadAsync<BenchmarkBlockReport>(path, cancellationToken));
    }
    var report = BenchmarkAnalyzer.Analyze(manifest, parity, apiComparison, blocks);
    var output = EvidencePaths.ResolveOutput(options.Require("output"));
    await JsonFiles.WriteAsync(output, report, cancellationToken);
    Console.WriteLine(
        $"analysis={output} blocks={blocks.Count} passed={report.Passed} " +
        $"failures={report.Failures.Count}");
    return report.Passed ? 0 : 7;
}

static async Task<int> FinalizeAcceptanceAsync(
    CommandArguments options,
    CancellationToken cancellationToken)
{
    var manifest = await ReadManifestAsync(
        EvidencePaths.ResolveInput(options.Require("manifest")),
        cancellationToken);
    var analysis = await JsonFiles.ReadAsync<BenchmarkAnalysisReport>(
        EvidencePaths.ResolveInput(options.Require("analysis")),
        cancellationToken);
    var rollback = await JsonFiles.ReadAsync<RollbackVerificationEvidence>(
        EvidencePaths.ResolveInput(options.Require("rollback-evidence")),
        cancellationToken);
    var recovery = await JsonFiles.ReadAsync<RollbackVerificationEvidence>(
        EvidencePaths.ResolveInput(options.Require("recovery-evidence")),
        cancellationToken);
    var finalRuntime = await JsonFiles.ReadAsync<RollbackVerificationEvidence>(
        EvidencePaths.ResolveInput(options.Require("final-evidence")),
        cancellationToken);
    var finalQuiescencePath =
        EvidencePaths.ResolveInput(options.Require("final-quiescence"));
    var finalQuiescence = await JsonFiles.ReadAsync<RolloutPreflightReport>(
        finalQuiescencePath,
        cancellationToken);
    var finalQuiescenceHashPath =
        EvidencePaths.ResolveInput(options.Require("final-quiescence-sha256"));
    var expectedHash = (await File.ReadAllTextAsync(
            finalQuiescenceHashPath,
            cancellationToken))
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault()
        ?? "";
    var actualHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                await File.ReadAllBytesAsync(
                    finalQuiescencePath,
                    cancellationToken)))
        .ToLowerInvariant();
    if (!string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
        throw new InvalidDataException("Final quiescence SHA-256 mismatch.");
    var report = RolloutAcceptance.Finalize(
        manifest,
        analysis,
        rollback,
        recovery,
        finalRuntime,
        finalQuiescence,
        actualHash);
    var output = EvidencePaths.ResolveOutput(options.Require("output"));
    await JsonFiles.WriteAtomicAsync(output, report, cancellationToken);
    Console.WriteLine($"acceptance={output} passed={report.Passed}");
    return report.Passed ? 0 : 10;
}

static async Task<int> ValidateManifestAsync(
    CommandArguments options,
    CancellationToken cancellationToken)
{
    var manifest = await ReadManifestAsync(
        EvidencePaths.ResolveInput(options.Require("manifest")),
        cancellationToken);
    Console.WriteLine(
        $"manifest={manifest.SelectionFingerprint} image={manifest.ServiceImageReference} " +
        $"mount={manifest.EvidenceMountSource}:{manifest.EvidenceMountFileSystem}");
    return 0;
}

static async Task<RolloutManifest> ReadManifestAsync(
    string path,
    CancellationToken cancellationToken)
{
    var manifest = await JsonFiles.ReadAsync<RolloutManifest>(path, cancellationToken);
    if (manifest.SchemaVersion != 4)
    {
        throw new InvalidDataException(
            $"Unsupported manifest schema version: {manifest.SchemaVersion}");
    }
    RolloutImagePin.Validate(manifest.ServiceImageReference, manifest.ServiceImageId);
    if (string.IsNullOrWhiteSpace(manifest.WorkerContainerId)
        || string.IsNullOrWhiteSpace(manifest.WorkerImageReference)
        || !RolloutImagePin.IsValidImageId(manifest.WorkerImageId)
        || manifest.WorkerContainerStatus is not ("exited" or "created")
        || string.IsNullOrWhiteSpace(manifest.WorkerContainerState))
    {
        throw new InvalidDataException("Manifest worker runtime pin is invalid.");
    }
    if (string.IsNullOrWhiteSpace(manifest.DatabaseIdentity.DatabaseName)
        || string.IsNullOrWhiteSpace(manifest.DatabaseIdentity.SystemIdentifier)
        || string.IsNullOrWhiteSpace(manifest.DatabaseIdentity.ServerAddress)
        || manifest.DatabaseIdentity.ServerPort <= 0
        || string.IsNullOrWhiteSpace(manifest.DatabaseIdentity.UnixSocketDirectories)
        || string.IsNullOrWhiteSpace(manifest.ServiceDatabaseTarget.Host)
        || manifest.ServiceDatabaseTarget.Port <= 0
        || string.IsNullOrWhiteSpace(manifest.ServiceDatabaseTarget.Database)
        || string.IsNullOrWhiteSpace(manifest.ServiceDatabaseTarget.Username)
        || string.IsNullOrWhiteSpace(manifest.PostgresContainerId)
        || string.IsNullOrWhiteSpace(manifest.PostgresImageReference)
        || !RolloutImagePin.IsValidImageId(manifest.PostgresImageId)
        || manifest.PostgresNetworkNames.Count == 0
        || manifest.PostgresNetworkAliases.Count == 0
        || manifest.PostgresServerAddresses.Count == 0
        || manifest.PostgresNetworkBindings.Count != 1)
    {
        throw new InvalidDataException("Manifest PostgreSQL runtime binding is invalid.");
    }
    var databaseAttestation = ReadOnlyPostgres.CompareDatabaseIdentity(
        manifest,
        manifest.DatabaseIdentity);
    if (!databaseAttestation.Passed)
    {
        throw new InvalidDataException(
            "Manifest PostgreSQL runtime binding is inconsistent: " +
            string.Join(", ", databaseAttestation.Failures));
    }
    RolloutEvidenceMount.Validate(
        manifest.EvidenceMountTarget,
        manifest.EvidenceMountSource,
        manifest.EvidenceMountFileSystem);
    var fingerprint = DeterministicRollout.ComputeManifestFingerprint(manifest);
    if (!string.Equals(fingerprint, manifest.SelectionFingerprint, StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            $"Manifest fingerprint mismatch: stored={manifest.SelectionFingerprint} computed={fingerprint}");
    }
    return manifest;
}

static int RunSelfTest()
{
    if (DeterministicRollout.CalculateThreshold(99_999, 1) != 100_098)
        throw new InvalidOperationException("C# threshold truncation self-test failed.");
    if (DockerStats.ParseByteSize("1.5GiB") != 1_610_612_736)
        throw new InvalidOperationException("Current-memory parser self-test failed.");
    const string selfTestImage =
        "ghcr.io/sfenton/fstservice:self-test@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string selfTestImageId =
        "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    if (!RolloutImagePin.IsValid(selfTestImage, selfTestImageId))
        throw new InvalidOperationException("Immutable image-pin self-test failed.");
    var networkBindingJson = JsonSerializer.Serialize(
        new[]
        {
            new PostgresNetworkBinding
            {
                NetworkName = "self-test-network",
                NetworkId = "self-test-network-id",
                ServiceAlias = "postgres",
                ExclusiveOwnerContainerId = "self-test-postgres",
                ServerAddresses = ["192.0.2.10"],
            },
        },
        RolloutJson.Options);
    var networkBindings =
        JsonSerializer.Deserialize<IReadOnlyList<PostgresNetworkBinding>>(
            networkBindingJson,
            RolloutJson.Options);
    if (networkBindings is null
        || networkBindings.Count != 1
        || networkBindings[0].ExclusiveOwnerContainerId != "self-test-postgres")
    {
        throw new InvalidOperationException(
            "Exclusive PostgreSQL network-binding self-test failed.");
    }
    RolloutEvidenceMount.Validate(
        RolloutEvidenceMount.RequiredTarget,
        "/dev/self-test",
        "ext4");
    if (RolloutStatistics.ChangePercent(0, 1) is not null)
        throw new InvalidOperationException("Zero-baseline resource self-test failed.");
    _ = System.Text.Json.JsonSerializer.Serialize(
        new ResourceAnalysis
        {
            Mode = "self-test",
            TempBytesBaselineZero = true,
            TempBytesChangePercent = null,
        },
        RolloutJson.Options);

    var scopes = Enumerable.Range(0, 9)
        .Select(index => new ScopeEvidence
        {
            Id = $"scope-{index}",
            SongId = $"song-{index}",
            Instrument = $"instrument-{index}",
            SourceClass = ScopeSourceClass.Current,
            PublishedRowCount = 200,
        })
        .ToArray();
    var first = DeterministicRollout.StableOrder(scopes, 1234, "self-test")
        .Select(static item => item.Id)
        .ToArray();
    var second = DeterministicRollout.StableOrder(scopes.Reverse(), 1234, "self-test")
        .Select(static item => item.Id)
        .ToArray();
    if (!first.SequenceEqual(second, StringComparer.Ordinal))
        throw new InvalidOperationException("Deterministic selection self-test failed.");

    var differences = ParityComparison.CompareLeaderboard(
        1,
        [new ComparableLeaderboardRow { AccountId = "a", Score = 10, Rank = 1 }],
        1,
        [new ComparableLeaderboardRow { AccountId = "a", Score = 10, Rank = 2 }]);
    if (!differences.Any(static difference => difference.Field == "rank"))
        throw new InvalidOperationException("Injected-difference self-test failed.");

    var manifest = new RolloutManifest
    {
        Seed = 1234,
        RequiredInstruments = ["Solo_Guitar"],
        ApiWorkloads =
        [
            new ApiWorkload
            {
                Id = "core",
                Kind = "single",
                Path = "/api/test",
                Core = true,
                Benchmark = true,
            },
        ],
    };
    var schedule = DeterministicRollout.BuildSchedule(manifest, 1234);
    foreach (var group in schedule.GroupBy(static item =>
                 (item.WorkloadId, item.Mode, item.Concurrency, item.AbbaBlock)))
    {
        var variants = group.OrderBy(static item => item.Position)
            .Select(static item => item.Variant)
            .ToArray();
        var valid = variants.SequenceEqual(["baseline", "candidate", "candidate", "baseline"])
                    || variants.SequenceEqual(["candidate", "baseline", "baseline", "candidate"]);
        if (!valid)
            throw new InvalidOperationException("ABBA schedule self-test failed.");
    }

    Console.WriteLine(
        "{\"selfTest\":\"passed\",\"model\":\"gpt-5.6-sol\",\"reasoning\":\"max\",\"context\":\"long_context\"}");
    return 0;
}

static void PrintUsage()
{
    Console.WriteLine(
        """
        FstStoredRankRollout commands:
          self-test
          manifest --output PATH --service-image TAG@sha256:DIGEST
              --service-image-id sha256:ID --worker-container-id ID
              --worker-image REF --worker-image-id sha256:ID
              --worker-container-status exited|created --worker-container-state STATE
              --service-db-host HOST --service-db-port PORT
              --service-db-name DB --service-db-username USER
              --postgres-container-id ID --postgres-image REF
              --postgres-image-id sha256:ID --postgres-network-names CSV
              --postgres-network-aliases CSV --postgres-server-addresses CSV
              --postgres-network-bindings-json JSON
              --evidence-mount-target PATH
              --evidence-mount-source DEVICE --evidence-mount-filesystem TYPE
              [--seed N] [--connection-env NAME]
          preflight --expected-published-scrape ID --output PATH [--manifest PATH]
          guard --manifest PATH --output PATH
          db-attest --manifest PATH [--output PATH]
          row-parity --manifest PATH --output PATH
          api-capture --manifest PATH --base-url URL --variant NAME --output-dir DIR
          api-compare --baseline-report PATH --candidate-report PATH --output PATH
          schedule --manifest PATH --output PATH.tsv
          benchmark-block --manifest PATH --workload-id ID --sequence N --mode cold|warm
              --concurrency 1|8 --variant baseline|candidate --request-count N
              --base-url URL --postgres-container NAME
              [--warm-request-starts-per-second N] --output PATH
          analyze --manifest PATH --row-parity PATH --api-comparison PATH
              --blocks-dir DIR --output PATH
          finalize-acceptance --manifest PATH --analysis PATH
              --rollback-evidence PATH --recovery-evidence PATH
              --final-evidence PATH --final-quiescence PATH
              --final-quiescence-sha256 PATH --output PATH
          validate-manifest --manifest PATH

        Database commands read the connection string only from
        FST_STORED_RANK_CONNECTION_STRING (or --connection-env NAME). Role
        validation also requires FST_STORED_RANK_VISIBILITY_PROBE_CONNECTION_STRING
        for a distinct controlled cross-role pg_stat_activity session. Every report
        path must remain under FST_STORED_RANK_EVIDENCE_ROOT, itself under the
        configured 4 TB FST evidence directory.
        """);
}
