using FSTService.Scraping.Replay;

namespace FSTService.Tests.Unit;

public sealed class ReplayContractTests
{
    [Fact]
    public void ExecutionCommandRequiresExplicitNoPublicationAndOptions()
    {
        var command = ReplayCommand.Parse(
        [
            "--replay-parent-package=/approved/parent",
            "--replay-package",
            "/approved/input",
            "--replay-phase",
            ReplayPhaseCatalog.BandMaintenancePhaseId,
            "--replay-subphase",
            ReplayPhaseCatalog.CurrentProjectionSubphaseId,
            "--replay-output",
            "/approved/output",
            "--replay-id",
            "replay-1",
            "--replay-attempt",
            "2",
            "--no-publication",
        ]);

        Assert.Equal(ReplayCommandKind.Execute, command.Kind);
        Assert.Equal("/approved/parent", command.ParentPackagePath);
        Assert.Equal("/approved/input", command.InputPackagePath);
        Assert.Equal("/approved/output", command.OutputPath);
        Assert.Equal(2, command.Attempt);
    }

    [Theory]
    [InlineData("--replay-package", "input")]
    [InlineData("--replay-phase", "post.band_maintenance")]
    [InlineData("--replay-output", "output")]
    public void ReplayCommandRejectsIncompleteOrPublicationCapableInput(
        string flag,
        string value)
    {
        var exception = Assert.Throws<ReplayException>(() =>
            ReplayCommand.Parse([flag, value]));

        Assert.Equal(ReplayFailureKind.Usage, exception.Kind);
        Assert.Equal(ReplayExitCode.Usage, exception.ExitCode);
    }

    [Fact]
    public void ReplayCommandRejectsMixedHostingMode()
    {
        var exception = Assert.Throws<ReplayException>(() =>
            ReplayCommand.Parse(
            [
                "--replay-package", "input",
                "--once",
                "--no-publication",
            ]));

        Assert.Contains("--once", exception.Message);
    }

    [Fact]
    public void ComparisonCommandIsExplicitAndSeparate()
    {
        var command = ReplayCommand.Parse(
        [
            "--replay-compare-baseline", "baseline",
            "--replay-compare-candidate", "candidate",
            "--replay-comparison-output", "comparison.json",
            "--replay-baseline-image-digest",
            $"sha256:{new string('a', 64)}",
            "--replay-candidate-image-digest",
            $"sha256:{new string('b', 64)}",
            "--replay-baseline-revision",
            new string('c', 40),
            "--replay-candidate-revision",
            new string('d', 40),
            "--replay-baseline-git-commit",
            new string('e', 40),
            "--replay-candidate-git-commit",
            new string('f', 40),
            "--replay-baseline-attempt", "1",
            "--replay-candidate-attempt", "2",
            "--no-publication",
        ]);

        Assert.Equal(ReplayCommandKind.Compare, command.Kind);
        Assert.Equal("baseline", command.BaselinePath);
        Assert.Equal("candidate", command.CandidatePath);
        Assert.Equal(
            2,
            command.ComparisonExpectations!.CandidateAttempt);
    }

    [Fact]
    public void PhaseCatalogRejectsUnknownAndUnsupportedPhases()
    {
        var unknown = Assert.Throws<ReplayException>(() =>
            ReplayPhaseCatalog.Resolve(
                "post.unknown",
                ReplayPhaseCatalog.CurrentProjectionSubphaseId));
        var unsupported = Assert.Throws<ReplayException>(() =>
            ReplayPhaseCatalog.Resolve(
                "post.band_extraction",
                "run"));
        var supported = ReplayPhaseCatalog.Resolve(
            ReplayPhaseCatalog.BandMaintenancePhaseId,
            ReplayPhaseCatalog.CurrentProjectionSubphaseId);

        Assert.Equal(ReplayExitCode.Usage, unknown.ExitCode);
        Assert.Equal(ReplayExitCode.Usage, unsupported.ExitCode);
        Assert.False(supported.SupportsPublication);
        Assert.False(supported.SupportsProviderNetwork);
        Assert.True(supported.PublicationCriticalInProduction);
        Assert.Equal(
            ["post.band_extraction"],
            supported.DependencyPhaseIds);
    }

    [Fact]
    public void RootAdmissionConfinesAndSeparatesPackages()
    {
        using var root = new ReplayTestDirectory("root-admission");
        var parent = Directory.CreateDirectory(
            Path.Combine(root.Path, "parent")).FullName;
        var input = Directory.CreateDirectory(
            Path.Combine(root.Path, "input")).FullName;
        var policy = new ReplayRootAdmission(
            new ReplayRootPolicyOptions(
                root.Path,
                TestOnly: true,
                RollbackReserveBytes: 0));

        var admitted = policy.AdmitExecution(
            parent,
            input,
            Path.Combine(root.Path, "output"));

        Assert.Equal(parent, admitted.ParentPackage);
        Assert.Equal(input, admitted.InputPackage);
        Assert.EndsWith(
            $"{Path.DirectorySeparatorChar}output",
            admitted.OutputPackage);
        policy.RequireCapacity(1, 1);
    }

    [Fact]
    public void RootAdmissionRejectsOutsideNestedExistingAndMissingRoots()
    {
        using var root = new ReplayTestDirectory("root-rejection");
        using var outside = new ReplayTestDirectory("root-outside");
        var parent = Directory.CreateDirectory(
            Path.Combine(root.Path, "parent")).FullName;
        var input = Directory.CreateDirectory(
            Path.Combine(root.Path, "input")).FullName;
        var existingOutput = Directory.CreateDirectory(
            Path.Combine(root.Path, "existing")).FullName;
        var policy = new ReplayRootAdmission(
            new ReplayRootPolicyOptions(
                root.Path,
                TestOnly: true,
                RollbackReserveBytes: 0));

        AssertRootRejected(() => policy.AdmitExecution(
            parent,
            input,
            Path.Combine(outside.Path, "output")));
        AssertRootRejected(() => policy.AdmitExecution(
            parent,
            input,
            Path.Combine(input, "nested-output")));
        AssertRootRejected(() => policy.AdmitExecution(
            parent,
            input,
            existingOutput));
        AssertRootRejected(() => policy.AdmitExecution(
            parent,
            input,
            Path.Combine(
                root.Path,
                "nested",
                "..",
                "output")));
        AssertRootRejected(() => policy.AdmitExecution(
            parent,
            input,
            "relative-output"));
        AssertRootRejected(() => new ReplayRootAdmission(
            new ReplayRootPolicyOptions(
                Path.Combine(root.Path, "missing"),
                TestOnly: true,
                RollbackReserveBytes: 0)));
        AssertRootRejected(() => new ReplayRootAdmission(
            new ReplayRootPolicyOptions(
                root.Path,
                TestOnly: false,
                RollbackReserveBytes: 0)));
    }

    [Fact]
    public void RootAdmissionRejectsSymbolicLinkEscapeWhenSupported()
    {
        using var root = new ReplayTestDirectory("root-link");
        using var outside = new ReplayTestDirectory("root-link-outside");
        var parent = Directory.CreateDirectory(
            Path.Combine(root.Path, "parent")).FullName;
        var link = Path.Combine(root.Path, "linked-input");
        try
        {
            Directory.CreateSymbolicLink(link, outside.Path);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            PlatformNotSupportedException)
        {
            return;
        }
        var policy = new ReplayRootAdmission(
            new ReplayRootPolicyOptions(
                root.Path,
                TestOnly: true,
                RollbackReserveBytes: 0));

        AssertRootRejected(() => policy.AdmitExecution(
            parent,
            link,
            Path.Combine(root.Path, "output")));
    }

    [Theory]
    [InlineData("Host=postgres;Database=fst_replay_test;Username=test")]
    [InlineData("Host=127.0.0.1;Database=fstservice;Username=test")]
    [InlineData("Host=127.0.0.1;Database=postgres;Username=test")]
    [InlineData("Host=127.0.0.1,localhost;Database=fst_replay_test;Username=test")]
    public void TargetGuardRejectsProductionShapedConfiguration(
        string connectionString)
    {
        var exception = Assert.Throws<ReplayException>(() =>
            new ReplayDatabaseTargetGuard(
                connectionString,
                null));

        Assert.Equal(ReplayFailureKind.TargetRejected, exception.Kind);
    }

    [Fact]
    public void TargetGuardRejectsConfiguredProductionEquality()
    {
        const string replayConnection =
            "Host=127.0.0.1;Port=5432;Database=fst_replay_test;Username=test";
        const string productionConnection =
            "Host=127.0.0.1;Port=5432;Database=fstservice;Username=fst";

        var exception = Assert.Throws<ReplayException>(() =>
            new ReplayDatabaseTargetGuard(
                replayConnection,
                productionConnection));

        Assert.Equal(ReplayExitCode.TargetRejected, exception.ExitCode);
        Assert.DoesNotContain("Username", exception.Message);
    }

    [Fact]
    public async Task ProgramReplayPathFailsBeforeWebHostAndDotEnvLoading()
    {
        using var directory = new ReplayTestDirectory("program-startup");
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, ".env"),
            "ConnectionStrings__PostgreSQL=Host=fst-postgres;Database=fstservice");
        var result = await RunProgramAsync(
            directory.Path,
            "--replay-package", "missing",
            "--no-publication");

        Assert.Equal((int)ReplayExitCode.Usage, result.ExitCode);
        Assert.Contains("\"kind\":\"Usage\"", result.Output);
        Assert.DoesNotContain("Now listening", result.Output);
        Assert.DoesNotContain("fst-postgres", result.Output);
    }

    [Fact]
    public async Task ReplayEntryPointReturnsTypedUsageAndRootCodes()
    {
        var usage = await ReplayEntryPoint.RunAsync(
        [
            "--replay-package",
            "input",
            "--no-publication",
        ]);
        using var root = new ReplayTestDirectory(
            "entrypoint-root");
        var environment = new ReplayExecutionEnvironment(
            new ReplayRootPolicyOptions(
                root.Path,
                TestOnly: true,
                RollbackReserveBytes: 0),
            "Host=127.0.0.1;Database=fst_replay_unused;Username=test",
            null,
            new TierZeroBuildIdentity(
                new string('a', 40),
                $"sha256:{new string('b', 64)}",
                new string('c', 40),
                "1.0.198"),
            "replay-entrypoint-test");
        var rootRejected = await ReplayEntryPoint.RunAsync(
        [
            "--replay-compare-baseline",
            Path.Combine(root.Path, "missing-baseline"),
            "--replay-compare-candidate",
            Path.Combine(root.Path, "missing-candidate"),
            "--replay-comparison-output",
            Path.Combine(root.Path, "comparison.json"),
            "--replay-baseline-image-digest",
            $"sha256:{new string('b', 64)}",
            "--replay-candidate-image-digest",
            $"sha256:{new string('b', 64)}",
            "--replay-baseline-git-commit",
            new string('a', 40),
            "--replay-candidate-git-commit",
            new string('a', 40),
            "--replay-baseline-revision",
            new string('c', 40),
            "--replay-candidate-revision",
            new string('c', 40),
            "--replay-baseline-attempt", "1",
            "--replay-candidate-attempt", "2",
            "--no-publication",
        ],
            environment);

        Assert.Equal((int)ReplayExitCode.Usage, usage);
        Assert.Equal(
            (int)ReplayExitCode.RootRejected,
            rootRejected);
    }

    [Fact]
    public void ReplayEnvironmentRequiresAndNormalizesExplicitValues()
    {
        using var variables = new EnvironmentVariableScope(
            new Dictionary<string, string?>
            {
                ["FST_REPLAY_APPROVED_ROOT"] =
                    "/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/replay-test",
                ["FST_REPLAY_APPROVED_DEVICE"] = "259:1",
                ["FST_REPLAY_ROLLBACK_RESERVE_BYTES"] = "1234",
                ["FST_REPLAY_POSTGRES_CONNECTION"] =
                    "Host=127.0.0.1;Database=fst_replay_test;Username=test",
                ["FST_REPLAY_GIT_COMMIT"] = new string('a', 40),
                ["FST_REPLAY_IMAGE_DIGEST"] =
                    $"sha256:{new string('b', 64)}",
                ["FST_REPLAY_IMAGE_REVISION"] =
                    new string('c', 40),
            });

        var environment =
            ReplayExecutionEnvironment.FromProcessEnvironment();

        Assert.Equal(
            1234,
            environment.RootPolicy.RollbackReserveBytes);
        Assert.Equal(
            "259:1",
            environment.RootPolicy.ExpectedFileSystemDevice);
        Assert.Equal(
            new string('a', 40),
            environment.Implementation.GitCommit);
    }

    private static void AssertRootRejected(Action action)
    {
        var exception = Assert.Throws<ReplayException>(action);
        Assert.Equal(ReplayExitCode.RootRejected, exception.ExitCode);
    }

    private static async Task<ProgramResult> RunProgramAsync(
        string workingDirectory,
        params string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start FSTService.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(timeout.Token);
        await Task.WhenAll(stdout, stderr);
        return new ProgramResult(
            process.ExitCode,
            string.Concat(stdout.Result, "\n", stderr.Result));
    }

    private sealed record ProgramResult(
        int ExitCode,
        string Output);

    private sealed class ReplayTestDirectory : IDisposable
    {
        internal ReplayTestDirectory(string name)
        {
            Path = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                ".test-temp",
                $"{name}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> _prior = [];

        internal EnvironmentVariableScope(
            IReadOnlyDictionary<string, string?> values)
        {
            foreach (var (key, value) in values)
            {
                _prior[key] =
                    Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        public void Dispose()
        {
            foreach (var (key, value) in _prior)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
