using System.Text.Json;
using FortniteFestival.Core;
using FSTService.Api;
using FSTService.Auth;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Scraping.Capture;
using FSTService.Scraping.Replay;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FSTService.Tests.Unit;

public sealed class CaptureOnlyModeTests
{
    [Fact]
    public void CommandParsesRequiredOptions()
    {
        var command = CaptureOnlyCommand.Parse(
        [
            "--capture-only",
            "--capture-output=/approved/capture-1",
            "--capture-id",
            "capture-1",
        ]);

        Assert.Equal(
            "/approved/capture-1",
            command.OutputPath);
        Assert.Equal("capture-1", command.CaptureId);
    }

    public static TheoryData<string[]> InvalidCommands
    {
        get
        {
            var data = new TheoryData<string[]>();
            data.Add([]);
            data.Add(["--capture-only"]);
            data.Add(["capture-only"]);
            data.Add(
            [
                "--capture-only",
                "--capture-output=",
                "--capture-id", "capture-1",
            ]);
            data.Add(
            [
                "--capture-output", "/approved/capture-1",
                "--capture-id", "capture-1",
            ]);
            data.Add(
            [
                "--capture-only",
                "--capture-only",
                "--capture-output", "/approved/capture-1",
                "--capture-id", "capture-1",
            ]);
            data.Add(
            [
                "--capture-only",
                "--capture-output", "/approved/capture-1",
                "--capture-output", "/approved/capture-2",
                "--capture-id", "capture-1",
            ]);
            data.Add(
            [
                "--capture-only",
                "--capture-output",
                "--capture-id", "capture-1",
            ]);
            data.Add(
            [
                "--capture-only",
                "--capture-output", "/approved/capture-1",
                "--capture-id", "../escape",
            ]);
            data.Add(
            [
                "--capture-only",
                "--capture-output", "/approved/capture-1",
                "--capture-id", "capture-1",
                "--once",
            ]);
            data.Add(
            [
                "--capture-only",
                "--capture-output", "/approved/capture-1",
                "--capture-id", "capture-1",
                "--replay-package", "/approved/replay",
            ]);
            data.Add(
            [
                "--capture-only",
                "--capture-output", "/approved/capture-1",
                "--capture-id", "capture-1",
                "--initialize-schema-only",
            ]);
            data.Add(
            [
                "--capture-only",
                "--capture-output", "/approved/capture-1",
                "--capture-id", "capture-1",
                "--api-only",
            ]);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(InvalidCommands))]
    public void CommandRejectsMissingDuplicateUnknownAndConflictingFlags(
        string[] arguments)
    {
        var exception =
            Assert.Throws<CaptureOnlyException>(() =>
                CaptureOnlyCommand.Parse(arguments));

        Assert.Equal(
            CaptureOnlyExitCode.Usage,
            exception.ExitCode);
    }

    [Fact]
    public void CapturePrefixForcesCaptureDispatch()
    {
        Assert.Throws<
            ArgumentNullException>(() =>
                CaptureOnlyCommand
                    .IsRequested(null!));
        Assert.Throws<
            ArgumentNullException>(() =>
                CaptureOnlyCommand
                    .Parse(null!));
        Assert.True(
            CaptureOnlyCommand.IsRequested(
            [
                "--capture-output",
                "/approved/capture-1",
            ]));
        Assert.False(
            CaptureOnlyCommand.IsRequested(
            [
                "--once",
            ]));
    }

    [Fact]
    public void EnvironmentRequiresExplicitStorageAndBuildIdentity()
    {
        using var variables =
            new EnvironmentVariableScope(
                new Dictionary<string, string?>
                {
                    [CaptureOnlyExecutionEnvironment
                        .ApprovedRootEnvironment] =
                        "/approved/capture",
                    [CaptureOnlyExecutionEnvironment
                        .ApprovedDeviceEnvironment] =
                        "259:1",
                    [CaptureOnlyExecutionEnvironment
                        .MaximumPackageBytesEnvironment] =
                        "268435456",
                    [CaptureOnlyExecutionEnvironment
                        .MinimumReserveBytesEnvironment] =
                        "1073741824",
                    [CaptureOnlyExecutionEnvironment
                        .MaximumRetainedPackagesEnvironment] =
                        "8",
                    [CaptureOnlyExecutionEnvironment
                        .GitCommitEnvironment] =
                        new string('a', 40),
                    [CaptureOnlyExecutionEnvironment
                        .ImageDigestEnvironment] =
                        $"sha256:{new string('b', 64)}",
                    [CaptureOnlyExecutionEnvironment
                        .ImageRevisionEnvironment] =
                        new string('c', 40),
                    [CaptureOnlyExecutionEnvironment
                        .ResponseShardBytesEnvironment] =
                        null,
                    [CaptureOnlyExecutionEnvironment
                        .PaginationMaximumScoresPathEnvironment] =
                        "/approved/capture/pagination-maxima.json",
                });

        var environment =
            CaptureOnlyExecutionEnvironment
                .FromProcessEnvironment();

        Assert.Equal(
            268435456,
            environment.StoragePolicy
                .MaximumPackageBytes);
        Assert.Equal(
            1073741824,
            environment.StoragePolicy
                .MinimumFreeSpaceReserveBytes);
        Assert.Equal(
            8,
            environment.StoragePolicy
                .MaximumRetainedSealedPackages);
        Assert.Equal(
            "/approved/capture/pagination-maxima.json",
            environment
                .PaginationMaximumScoresPath);

        Environment.SetEnvironmentVariable(
            CaptureOnlyExecutionEnvironment
                .MaximumPackageBytesEnvironment,
            null);
        var exception =
            Assert.Throws<CaptureOnlyException>(
                CaptureOnlyExecutionEnvironment
                    .FromProcessEnvironment);
        Assert.Equal(
            CaptureOnlyExitCode.Usage,
            exception.ExitCode);
    }

    [Fact]
    public void EnvironmentRejectsMalformedNumericAndIdentityValues()
    {
        using var directory =
            new CaptureTestDirectory(
                "invalid-environment");
        var valid =
            new Dictionary<string, string?>
            {
                [CaptureOnlyExecutionEnvironment
                    .ApprovedRootEnvironment] =
                    directory.Path,
                [CaptureOnlyExecutionEnvironment
                    .ApprovedDeviceEnvironment] =
                    TierZeroRegularFile
                        .GetFileSystemDeviceIdentity(
                            directory.Path),
                [CaptureOnlyExecutionEnvironment
                    .MaximumPackageBytesEnvironment] =
                    "268435456",
                [CaptureOnlyExecutionEnvironment
                    .MinimumReserveBytesEnvironment] =
                    "0",
                [CaptureOnlyExecutionEnvironment
                    .MaximumRetainedPackagesEnvironment] =
                    "2",
                [CaptureOnlyExecutionEnvironment
                    .GitCommitEnvironment] =
                    new string('a', 40),
                [CaptureOnlyExecutionEnvironment
                    .ImageDigestEnvironment] =
                    $"sha256:{new string('b', 64)}",
                [CaptureOnlyExecutionEnvironment
                    .ImageRevisionEnvironment] =
                    new string('c', 40),
                [CaptureOnlyExecutionEnvironment
                    .ResponseShardBytesEnvironment] =
                    "67108864",
                [CaptureOnlyExecutionEnvironment
                    .PaginationMaximumScoresPathEnvironment] =
                    null,
            };
        var invalidCases =
            new (string Key, string? Value)[]
            {
                (
                    CaptureOnlyExecutionEnvironment
                        .ApprovedRootEnvironment,
                    null),
                (
                    CaptureOnlyExecutionEnvironment
                        .MaximumPackageBytesEnvironment,
                    "not-a-number"),
                (
                    CaptureOnlyExecutionEnvironment
                        .MaximumPackageBytesEnvironment,
                    CaptureRootAdmission
                        .PreMetadataFinalPackageAllowanceBytes
                        .ToString()),
                (
                    CaptureOnlyExecutionEnvironment
                        .MinimumReserveBytesEnvironment,
                    "-1"),
                (
                    CaptureOnlyExecutionEnvironment
                        .MaximumRetainedPackagesEnvironment,
                    "not-a-number"),
                (
                    CaptureOnlyExecutionEnvironment
                        .MaximumRetainedPackagesEnvironment,
                    "0"),
                (
                    CaptureOnlyExecutionEnvironment
                        .ResponseShardBytesEnvironment,
                    "not-a-number"),
                (
                    CaptureOnlyExecutionEnvironment
                        .ResponseShardBytesEnvironment,
                    CapturePackageFormat
                        .MaximumResponseRecordBytes
                        .ToString()),
                (
                    CaptureOnlyExecutionEnvironment
                        .GitCommitEnvironment,
                    "invalid"),
                (
                    CaptureOnlyExecutionEnvironment
                        .ImageDigestEnvironment,
                    "sha256:invalid"),
                (
                    CaptureOnlyExecutionEnvironment
                        .ImageRevisionEnvironment,
                    "invalid"),
            };

        foreach (var (key, value) in
                 invalidCases)
        {
            var variables =
                new Dictionary<string, string?>(
                    valid,
                    StringComparer.Ordinal)
                {
                    [key] = value,
                };
            using var scope =
                new EnvironmentVariableScope(
                    variables);
            var exception =
                Assert.Throws<
                    CaptureOnlyException>(
                    CaptureOnlyExecutionEnvironment
                        .FromProcessEnvironment);
            Assert.Equal(
                CaptureOnlyExitCode.Usage,
                exception.ExitCode);
        }
    }

    [Fact]
    public async Task PublicEntryPointLoadsEnvironmentBeforeValidation()
    {
        using var directory =
                new CaptureTestDirectory(
                    "public-entrypoint");
        var variables =
                ValidCaptureEnvironmentValues(
                    directory.Path);
        variables["Scraper__DegreeOfParallelism"] =
                "0";
        using var scope =
                new EnvironmentVariableScope(
                    variables);

        var exitCode =
                await CaptureOnlyEntryPoint.RunAsync(
                [
                    "--capture-only",
                    "--capture-output",
                    Path.Combine(
                        directory.Path,
                        "capture"),
                    "--capture-id",
                    "public-entrypoint",
                ]);

        Assert.Equal(
                (int)CaptureOnlyExitCode.Usage,
                exitCode);
    }

    [Fact]
    public async Task PublicEntryPointRejectsInvalidCommandBeforeHostStartup()
    {
        var exitCode =
            await CaptureOnlyEntryPoint.RunAsync(
            [
                "--capture-only",
            ]);

        Assert.Equal(
            (int)CaptureOnlyExitCode.Usage,
            exitCode);
    }

    [Fact]
    public void EnvironmentFileLoadsAssignmentsAndIgnoresNonAssignments()
    {
        using var directory =
            new CaptureTestDirectory(
                "environment-file");
        var path = Path.Combine(
            directory.Path,
            ".env");
        const string first =
            "FST_CAPTURE_TEST_ENV_FIRST";
        const string second =
            "FST_CAPTURE_TEST_ENV_SECOND";
        using var variables =
            new EnvironmentVariableScope(
                new Dictionary<string, string?>
                {
                    [first] = null,
                    [second] = null,
                });
        File.WriteAllText(
            path,
            $"""
            # comment

            malformed
            =missing-key
            {first}=value-one
            {second}="value two"
            """);

        CaptureEnvironmentFile.LoadFile(path);
        CaptureEnvironmentFile.LoadFile(
            Path.Combine(
                directory.Path,
                "missing.env"));

        Assert.Equal(
            "value-one",
            Environment.GetEnvironmentVariable(
                first));
        Assert.Equal(
            "value two",
            Environment.GetEnvironmentVariable(
                second));
        Assert.Throws<ArgumentException>(
            () => CaptureEnvironmentFile
                .LoadFile(""));
    }

    [Fact]
    public void ScraperOptionsLoadEnvironmentOverrides()
    {
        using var variables =
            new EnvironmentVariableScope(
                new Dictionary<string, string?>
                {
                    ["Scraper__DegreeOfParallelism"] =
                        "7",
                    ["Scraper__QueryLead"] =
                        "false",
                });

        var options =
            CaptureOnlyEntryPoint
                .LoadScraperOptions();

        Assert.Equal(
            7,
            options.DegreeOfParallelism);
        Assert.False(options.QueryLead);
    }

    [Fact]
    public async Task EntryPointMapsTypedAndUnexpectedExitCodes()
    {
        using var directory =
            new CaptureTestDirectory(
                "entrypoint-codes");
        var command = new CaptureOnlyCommand(
            Path.Combine(directory.Path, "capture"),
            "capture-1");
        var environment =
            CreateEnvironment(directory.Path);
        var options = CreateOptions();

        using (var output = new StringWriter())
        {
            var success =
                await CaptureOnlyEntryPoint.RunAsync(
                    command,
                    environment,
                    options,
                    new CaptureOnlyCompositionOverrides(
                        Runner: new StubRunner(
                            static (capture, _) =>
                                Task.FromResult(
                                    new CaptureOnlyResult(
                                        capture.CaptureId,
                                        Hash("root"),
                                        1,
                                        1,
                                        1,
                                        1,
                                        1)))),
                    output,
                    CancellationToken.None);
            Assert.Equal(
                (int)CaptureOnlyExitCode.Success,
                success);
            Assert.Contains(
                $"\"packageRootHash\":\"{Hash("root")}\"",
                output.ToString());
            Assert.DoesNotContain(
                command.OutputPath,
                output.ToString());
        }

        foreach (var (failureKind, exitCode) in
                 Enum.GetValues<CaptureOnlyExitCode>()
                     .Where(static code =>
                         code is not
                             CaptureOnlyExitCode.Success and
                         not CaptureOnlyExitCode
                             .UnexpectedFailure and
                         not CaptureOnlyExitCode.Cancelled)
                     .Select(code => (
                         FailureKindFor(code),
                         code)))
        {
            using var output = new StringWriter();
            var runner = new StubRunner((_, _) =>
                throw new CaptureOnlyException(
                    failureKind,
                    exitCode,
                    "sanitized"));
            var actual =
                await CaptureOnlyEntryPoint.RunAsync(
                    command,
                    environment,
                    options,
                    new CaptureOnlyCompositionOverrides(
                        Runner: runner),
                    output,
                    CancellationToken.None);

            Assert.Equal((int)exitCode, actual);
            Assert.Contains(
                $"\"exitCode\":{(int)exitCode}",
                output.ToString());
        }

        using (var output = new StringWriter())
        {
            using var cancellation =
                new CancellationTokenSource();
            cancellation.Cancel();
            var cancelled =
                await CaptureOnlyEntryPoint.RunAsync(
                    command,
                    environment,
                    options,
                    new CaptureOnlyCompositionOverrides(
                        Runner: new StubRunner(
                            static (_, _) =>
                                throw new
                                    OperationCanceledException())),
                    output,
                    cancellation.Token);
            Assert.Equal(
                (int)CaptureOnlyExitCode.Cancelled,
                cancelled);
        }

        using (var output = new StringWriter())
        {
            var unexpectedCancellation =
                await CaptureOnlyEntryPoint.RunAsync(
                    command,
                    environment,
                    options,
                    new CaptureOnlyCompositionOverrides(
                        Runner: new StubRunner(
                            static (_, _) =>
                                throw new
                                    OperationCanceledException())),
                    output,
                    CancellationToken.None);
            Assert.Equal(
                (int)CaptureOnlyExitCode
                    .UnexpectedFailure,
                unexpectedCancellation);
        }

        using (var output = new StringWriter())
        {
            var unexpected =
                await CaptureOnlyEntryPoint.RunAsync(
                    command,
                    environment,
                    options,
                    new CaptureOnlyCompositionOverrides(
                        Runner: new StubRunner(
                            static (_, _) =>
                                throw new
                                    InvalidOperationException(
                                        "secret-value"))),
                    output,
                    CancellationToken.None);
            Assert.Equal(
                (int)CaptureOnlyExitCode
                    .UnexpectedFailure,
                unexpected);
            Assert.DoesNotContain(
                "secret-value",
                output.ToString());
        }
    }

    [Fact]
    public async Task ProgramCapturePathFailsBeforeWebHostAndDatabaseSetup()
    {
        using var directory =
            new CaptureTestDirectory(
                "program-startup");
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, ".env"),
            "ConnectionStrings__PostgreSQL=Host=fst-postgres;Database=fstservice");
        var result = await RunProgramAsync(
            directory.Path,
            "--capture-only",
            "--capture-output",
            Path.Combine(directory.Path, "capture"),
            "--capture-id",
            "capture-1",
            "--once");

        Assert.Equal(
            (int)CaptureOnlyExitCode.Usage,
            result.ExitCode);
        Assert.Contains(
            "\"kind\":\"Usage\"",
            result.Output);
        Assert.DoesNotContain(
            "Now listening",
            result.Output);
        Assert.DoesNotContain(
            "fst-postgres",
            result.Output);
    }

    [Fact]
    public void RootAdmissionEnforcesNewDirectChildAndDevice()
    {
        using var directory =
            new CaptureTestDirectory(
                "root-admission");
        var storage = new TestStorageProbe();
        var admission = new CaptureRootAdmission(
            new CaptureRootPolicyOptions(
                directory.Path,
                ExpectedFileSystemDevice: null,
                TestOnly: true),
            storage);
        var output = Path.Combine(
            directory.Path,
            "capture-1");
        using var outside =
            new CaptureTestDirectory(
                "root-outside");

        var admitted = admission.AdmitOutput(output);

        Assert.Equal(output, admitted.OutputPackage);
        AssertCaptureFailure(
            CaptureOnlyExitCode.RootRejected,
            () => admission.AdmitOutput(
                Path.Combine(
                    outside.Path,
                    "capture-outside")));
        Directory.CreateDirectory(
            Path.Combine(directory.Path, "nested"));
        AssertCaptureFailure(
            CaptureOnlyExitCode.RootRejected,
            () => admission.AdmitOutput(
                Path.Combine(
                    directory.Path,
                    "nested",
                    "capture-2")));
        Directory.CreateDirectory(output);
        AssertCaptureFailure(
            CaptureOnlyExitCode.RootRejected,
            () => admission.AdmitOutput(output));
        AssertCaptureFailure(
            CaptureOnlyExitCode.RootRejected,
            () => new CaptureRootAdmission(
                new CaptureRootPolicyOptions(
                    directory.Path,
                    "wrong-device",
                    TestOnly: true),
                storage));

        using var sealedPackageRoot =
            new CaptureTestDirectory(
                "sealed-package-root");
        File.WriteAllText(
            Path.Combine(
                sealedPackageRoot.Path,
                TierZeroEvidenceFormat.ManifestFileName),
            "{}");
        AssertCaptureFailure(
            CaptureOnlyExitCode.RootRejected,
            () => new CaptureRootAdmission(
                new CaptureRootPolicyOptions(
                    sealedPackageRoot.Path,
                    null,
                    TestOnly: true),
                storage));

        using var packageAncestor =
            new CaptureTestDirectory(
                "sealed-package-ancestor");
        File.WriteAllText(
            Path.Combine(
                packageAncestor.Path,
                TierZeroEvidenceFormat.ManifestFileName),
            "{}");
        var nestedRoot = Path.Combine(
            packageAncestor.Path,
            "capture",
            "responses");
        Directory.CreateDirectory(nestedRoot);
        AssertCaptureFailure(
            CaptureOnlyExitCode.RootRejected,
            () => new CaptureRootAdmission(
                new CaptureRootPolicyOptions(
                    nestedRoot,
                    null,
                    TestOnly: true),
                storage));
    }

    [Fact]
    public void RootAdmissionRejectsCapacityAndRetention()
    {
        using var directory =
            new CaptureTestDirectory(
                "storage-admission");
        var policy = new CapturePackageStoragePolicy(
            MaximumPackageBytes: 100,
            MinimumFreeSpaceReserveBytes: 50,
            MaximumRetainedSealedPackages: 2);
        var output = Path.Combine(
            directory.Path,
            "capture-1");

        var lowSpace = new TestStorageProbe
        {
            AvailableFreeSpaceBytes = 149,
        };
        var lowSpaceAdmission =
            new CaptureRootAdmission(
                new CaptureRootPolicyOptions(
                    directory.Path,
                    null,
                    TestOnly: true),
                lowSpace);
        var admitted =
            lowSpaceAdmission.AdmitOutput(output);
        AssertCaptureFailure(
            CaptureOnlyExitCode.AdmissionRejected,
            () => lowSpaceAdmission.Preflight(
                admitted,
                policy,
                CancellationToken.None));

        var retained = new TestStorageProbe
        {
            AvailableFreeSpaceBytes = 1_000,
            RetainedSealedPackages = 2,
        };
        var retainedAdmission =
            new CaptureRootAdmission(
                new CaptureRootPolicyOptions(
                    directory.Path,
                    null,
                    TestOnly: true),
                retained);
        AssertCaptureFailure(
            CaptureOnlyExitCode.AdmissionRejected,
            () => retainedAdmission.Preflight(
                retainedAdmission.AdmitOutput(
                    output),
                policy,
                CancellationToken.None));

        var scratchPressure = new TestStorageProbe
        {
            AvailableFreeSpaceBytes =
                policy.MaximumPackageBytes +
                policy.MinimumFreeSpaceReserveBytes +
                CaptureRootAdmission
                    .SealWorkingSpaceAllowanceBytes +
                64,
        };
        var scratchAdmission =
            new CaptureRootAdmission(
                new CaptureRootPolicyOptions(
                    directory.Path,
                    null,
                    TestOnly: true),
                scratchPressure);
        AssertCaptureFailure(
            CaptureOnlyExitCode.AdmissionRejected,
            () => scratchAdmission.Preflight(
                scratchAdmission.AdmitOutput(
                    output),
                policy,
                CancellationToken.None,
                transientScratchBytes: 65));
    }

    [Fact]
    public async Task SealAdmissionRejectsInvalidMeasurementsAndStorageErrors()
    {
        using var directory =
                new CaptureTestDirectory(
                    "seal-admission-errors");
        var output = Path.Combine(
                directory.Path,
                "capture");
        Directory.CreateDirectory(output);
        var path = new AdmittedCapturePath(
                directory.Path,
                output);
        var policy =
                new CapturePackageStoragePolicy(
                    256L * 1024 * 1024,
                    0,
                    2);
        var lockPath = Path.Combine(
                directory.Path,
                "admission.lock");
        await using (var admission =
                new CaptureSealAdmission(
                    path,
                    policy,
                    new TestStorageProbe(),
                    new FileStream(
                        lockPath,
                        FileMode.CreateNew,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        1,
                        FileOptions.Asynchronous)))
        {
            AssertCaptureFailure(
                CaptureOnlyExitCode
                    .AdmissionRejected,
                () => admission
                    .RecheckBeforeMetadata(
                        -1,
                        CancellationToken.None));
            AssertCaptureFailure(
                CaptureOnlyExitCode
                    .AdmissionRejected,
                () => admission
                    .RecheckBeforeMetadata(
                        long.MaxValue,
                        CancellationToken.None));
            admission.RecheckBeforeSeal(
                CancellationToken.None);
        }

        var faultingLockPath = Path.Combine(
                directory.Path,
                "faulting.lock");
        await using var faultingAdmission =
                new CaptureSealAdmission(
                    path,
                    policy,
                    new FaultingStorageProbe(),
                    new FileStream(
                        faultingLockPath,
                        FileMode.CreateNew,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        1,
                        FileOptions.Asynchronous));
        AssertCaptureFailure(
                CaptureOnlyExitCode.AdmissionRejected,
                () => faultingAdmission
                    .RecheckBeforeSeal(
                        CancellationToken.None));
    }

    [Fact]
    public void DefaultShardGeometryCoversDocumentedResponseWorkload()
    {
        const long documentedResponseBytes =
            92_800_000_000;

        Assert.Equal(
            64 * 1024 * 1024,
            CaptureOnlyExecutionEnvironment
                .DefaultResponseShardMaximumBytes);
        CaptureResponseShardCapacity
            .EnsureAdmittedBudgetFits(
                CaptureOnlyExecutionEnvironment
                    .DefaultResponseShardMaximumBytes,
                documentedResponseBytes);

        var exception =
            Assert.Throws<CaptureOnlyException>(() =>
                CaptureResponseShardCapacity
                    .EnsureAdmittedBudgetFits(
                        32 * 1024 * 1024,
                        documentedResponseBytes));
        Assert.Equal(
            CaptureOnlyExitCode.AdmissionRejected,
            exception.ExitCode);
    }

    [Fact]
    public async Task ShardGeometryRejectsBeforeProviderTraffic()
    {
        using var directory =
            new CaptureTestDirectory(
                "shard-geometry");
        var authenticator =
            new CountingAuthenticator();
        var runner = CreateRunner(
            CreateEnvironment(
                directory.Path,
                responseShardBytes:
                    32 * 1024 * 1024,
                maximumPackageBytes:
                    92_800_000_000),
            CreateOptions(),
            new QueueCatalogSource(),
            new DictionaryPageSource(
                new Dictionary<
                    CapturePageKey,
                    CapturePageAcquisition>()),
            new TestStorageProbe
            {
                AvailableFreeSpaceBytes =
                    200_000_000_000,
            },
            authenticator);

        var exception =
            await Assert.ThrowsAsync<
                CaptureOnlyException>(() =>
                runner.ExecuteAsync(
                    new CaptureOnlyCommand(
                        Path.Combine(
                            directory.Path,
                            "capture"),
                        "capture-geometry"),
                    CancellationToken.None));

        Assert.Equal(
            CaptureOnlyExitCode.AdmissionRejected,
            exception.ExitCode);
        Assert.Equal(0, authenticator.CallCount);
    }

    [Fact]
    public async Task RootWideSealAdmissionSerializesRetentionDecision()
    {
        using var directory =
            new CaptureTestDirectory(
                "root-seal-admission");
        var storage = new TestStorageProbe
        {
            AvailableFreeSpaceBytes =
                512L * 1024 * 1024,
        };
        var policy = new CapturePackageStoragePolicy(
            128L * 1024 * 1024,
            0,
            1);
        var firstAdmission =
            new CaptureRootAdmission(
                new CaptureRootPolicyOptions(
                    directory.Path,
                    null,
                    TestOnly: true),
                storage);
        var secondAdmission =
            new CaptureRootAdmission(
                new CaptureRootPolicyOptions(
                    directory.Path,
                    null,
                    TestOnly: true),
                storage);
        var firstPath =
            firstAdmission.AdmitOutput(
                Path.Combine(
                    directory.Path,
                    "capture-a"));
        var secondPath =
            secondAdmission.AdmitOutput(
                Path.Combine(
                    directory.Path,
                    "capture-b"));
        Directory.CreateDirectory(
            firstPath.OutputPackage);
        Directory.CreateDirectory(
            secondPath.OutputPackage);

        var firstLease =
            await firstAdmission
                .AcquireCaptureAdmissionAsync(
                    firstPath,
                    policy,
                    CancellationToken.None);
        var secondStarted =
            new TaskCompletionSource(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        var second = Task.Run(async () =>
        {
            secondStarted.SetResult();
            await using var lease =
                await secondAdmission
                    .AcquireCaptureAdmissionAsync(
                        secondPath,
                        policy,
                        CancellationToken.None);
        });
        await secondStarted.Task;
        await Task.Delay(100);
        Assert.False(second.IsCompleted);

        storage.RetainedSealedPackages = 1;
        await firstLease.DisposeAsync();
        var exception =
            await Assert.ThrowsAsync<
                CaptureOnlyException>(
                async () => await second);
        Assert.Equal(
            CaptureOnlyExitCode.AdmissionRejected,
            exception.ExitCode);
        Assert.True(
            Directory.Exists(
                firstPath.OutputPackage));
        Assert.True(
            Directory.Exists(
                secondPath.OutputPackage));
    }

    [Fact]
    public async Task RootWideAdmissionLockRejectsSymbolicLink()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var directory =
            new CaptureTestDirectory(
                "root-lock-symlink");
        var target = Path.Combine(
            directory.Path,
            "lock-target");
        await File.WriteAllTextAsync(target, "");
        File.CreateSymbolicLink(
            Path.Combine(
                directory.Path,
                CaptureRootAdmission
                    .AdmissionLockFileName),
            target);
        var admission =
            new CaptureRootAdmission(
                new CaptureRootPolicyOptions(
                    directory.Path,
                    null,
                    TestOnly: true),
                new TestStorageProbe());
        var path = admission.AdmitOutput(
            Path.Combine(
                directory.Path,
                "capture"));
        Directory.CreateDirectory(
            path.OutputPackage);

        var exception =
            await Assert.ThrowsAsync<
                CaptureOnlyException>(() =>
                admission.AcquireCaptureAdmissionAsync(
                    path,
                    new CapturePackageStoragePolicy(
                        128L * 1024 * 1024,
                        0,
                        2),
                    CancellationToken.None));
        Assert.Equal(
            CaptureOnlyExitCode.AdmissionRejected,
            exception.ExitCode);
    }

    [Fact]
    public async Task PreflightRejectionOccursBeforeProviderTraffic()
    {
        using var directory =
            new CaptureTestDirectory(
                "preflight-before-provider");
        var authenticator =
            new CountingAuthenticator();
        var storage = new TestStorageProbe
        {
            AvailableFreeSpaceBytes = 1,
        };
        var runner = CreateRunner(
            CreateEnvironment(directory.Path),
            CreateOptions(),
            new QueueCatalogSource(),
            new DictionaryPageSource(
                new Dictionary<
                    CapturePageKey,
                    CapturePageAcquisition>()),
            storage,
            authenticator);
        var output = Path.Combine(
            directory.Path,
            "capture");

        var exception =
            await Assert.ThrowsAsync<
                CaptureOnlyException>(() =>
                runner.ExecuteAsync(
                    new CaptureOnlyCommand(
                        output,
                        "capture-preflight"),
                    CancellationToken.None));

        Assert.Equal(
            CaptureOnlyExitCode.AdmissionRejected,
            exception.ExitCode);
        Assert.Equal(0, authenticator.CallCount);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void CatalogBuilderUsesCanonicalSupportMapping()
    {
        var songs = new[]
        {
            CreateSong(
                "song-b",
                intensity => intensity.gr = 99),
            CreateSong(
                "song-a",
                intensity => intensity.pd = 0),
        };
        var providerHash = Hash(
            "provider-catalog");

        var catalog = CaptureCatalogBuilder.Build(
            songs,
            providerHash);

        Assert.Equal(
            ["song-a", "song-b"],
            catalog.Songs.Select(
                static song => song.SongId));
        Assert.All(
            catalog.Songs,
            song => Assert.Equal(
                CapturePackageFormat
                    .SoloInstrumentOrder.Count +
                CapturePackageFormat
                    .BandTypeOrder.Count,
                song.ScopeSupport.Count));
        Assert.Equal(
            CaptureCatalogSupportStatus
                .Unsupported,
            catalog.Songs[1].ScopeSupport
                .Single(static support =>
                    support.LeaderboardType ==
                    "Solo_Guitar")
                .Status);
        Assert.All(
            catalog.Songs.SelectMany(
                static song => song.ScopeSupport)
                .Where(static support =>
                    support.ScopeKind ==
                    CaptureScopeKind.Band),
            static support => Assert.Equal(
                CaptureCatalogSupportStatus
                    .Supported,
                support.Status));
        Assert.Equal(
            CapturePackageContract.SerializeCatalog(
                catalog),
            CapturePackageContract.SerializeCatalog(
                CaptureCatalogBuilder.Build(
                    songs.Reverse(),
                    providerHash)));
    }

    [Fact]
    public void CatalogBuilderRejectsInvalidProviderUniverses()
    {
        var providerHash =
                    Hash("provider-catalog");

        Assert.Throws<
                    ArgumentNullException>(() =>
                        CaptureCatalogBuilder.Build(
                            null!,
                            providerHash));
        Assert.Throws<
                    ArgumentException>(() =>
                        CaptureCatalogBuilder.Build(
                            [
                                CreateSong(
                                    "song-a",
                                    static _ => { }),
                            ],
                            "invalid"));
        Assert.Throws<
                    InvalidOperationException>(() =>
                        CaptureCatalogBuilder.Build(
                            [],
                            providerHash));
        Assert.Throws<
                    InvalidOperationException>(() =>
                        CaptureCatalogBuilder.Build(
                            [null!],
                            providerHash));
        Assert.Throws<
                    InvalidOperationException>(() =>
                        CaptureCatalogBuilder.Build(
                            [
                                CreateSong(
                                    "same",
                                    static _ => { }),
                                CreateSong(
                                    "same",
                                    static _ => { }),
                            ],
                            providerHash));
        Assert.Equal(
                    1,
                    CaptureCatalogBuilder
                        .CatalogVersionFromProviderHash(
                            new string('0', 64)));
    }

    [Fact]
    public async Task CatalogSourceRequiresExactProviderPayloadWithoutPersistence()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueJsonOk(
            """
            {
              "b": {
                "_title": "Song B",
                "track": {
                  "su": "song-b",
                  "tt": "Song B",
                  "in": { "gr": 99 }
                }
              },
              "a": {
                "_title": "Song A",
                "track": {
                  "su": "song-a",
                  "tt": "Song A",
                  "in": { "gr": 3 }
                }
              }
            }
            """);
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(
                "https://catalog.invalid"),
        };
        var source =
            new EpicCaptureCatalogSource(client);

        var result = await source.FetchAsync(
            CancellationToken.None);

        Assert.True(result.ProviderRequestSucceeded);
        Assert.True(result.IsExact);
        Assert.False(result.SafetyMergeApplied);
        Assert.False(result.IsReconstructed);
        Assert.Equal(0, result.ParseFailureCount);
        var catalog =
            Assert.IsType<CaptureCatalogArtifact>(
                result.Catalog);
        Assert.Equal(
            ["song-a", "song-b"],
            catalog.Songs.Select(
                static song => song.SongId));
        Assert.Equal(
            CaptureCatalogSupportStatus
                .Unsupported,
            catalog.Songs[1]
                .ScopeSupport.Single(
                    static support =>
                        support.LeaderboardType ==
                        "Solo_Guitar")
                .Status);
    }

    [Fact]
    public async Task CatalogSourceReportsProviderAndCanonicalFailures()
    {
        var failedHandler =
            new MockHttpMessageHandler();
        failedHandler.EnqueueError(
            System.Net.HttpStatusCode
                .ServiceUnavailable,
            """{"error":"unavailable"}""");
        using var failedClient =
            new HttpClient(failedHandler)
            {
                BaseAddress = new Uri(
                    "https://catalog.invalid"),
            };
        var failed =
            await new EpicCaptureCatalogSource(
                failedClient)
                .FetchAsync(
                    CancellationToken.None);
        Assert.False(
            failed.ProviderRequestSucceeded);
        Assert.False(failed.IsExact);
        Assert.Null(failed.Catalog);

        var duplicateHandler =
            new MockHttpMessageHandler();
        duplicateHandler.EnqueueJsonOk(
            """
            {
              "a": {
                "track": {
                  "su": "duplicate",
                  "tt": "Song A",
                  "in": { "gr": 3 }
                }
              },
              "b": {
                "track": {
                  "su": "duplicate",
                  "tt": "Song B",
                  "in": { "gr": 3 }
                }
              }
            }
            """);
        using var duplicateClient =
            new HttpClient(duplicateHandler)
            {
                BaseAddress = new Uri(
                    "https://catalog.invalid"),
            };
        var duplicate =
            await new EpicCaptureCatalogSource(
                duplicateClient)
                .FetchAsync(
                    CancellationToken.None);
        Assert.True(
            duplicate.ProviderRequestSucceeded);
        Assert.False(duplicate.IsExact);
        Assert.Equal(
            1,
            duplicate.ParseFailureCount);
        Assert.Null(duplicate.Catalog);
    }

    [Fact]
    public async Task EpicAuthenticatorRefreshesStoredCredential()
    {
        using var directory =
            new CaptureTestDirectory(
                "epic-auth-success");
        var credentialPath = Path.Combine(
            directory.Path,
            "device-auth.json");
        await File.WriteAllTextAsync(
            credentialPath,
            JsonSerializer.Serialize(
                new StoredCredentials
                {
                    AccountId =
                        "capture-account",
                    RefreshToken =
                        "stored-refresh",
                }));
        var handler =
            new MockHttpMessageHandler();
        handler.EnqueueJsonOk(
            """
            {
              "access_token": "new-access",
              "expires_in": 7200,
              "expires_at": "2026-09-13T18:00:00Z",
              "token_type": "bearer",
              "refresh_token": "new-refresh",
              "refresh_expires": 28800,
              "refresh_expires_at": "2026-09-14T00:00:00Z",
              "account_id": "capture-account",
              "client_id": "capture-client",
              "displayName": "Capture"
            }
            """);
        using var client =
            new HttpClient(handler);
        var options = CreateOptions();
        options.DeviceAuthPath =
            credentialPath;
        using var variables =
            new EnvironmentVariableScope(
                new Dictionary<string, string?>
                {
                    ["EPIC_CLIENT_ID"] =
                        "client-id",
                    ["EPIC_CLIENT_SECRET"] =
                        "client-secret",
                });
        var authenticator =
            new EpicCaptureAuthenticator(
                client,
                options,
                NullLoggerFactory.Instance);

        var authentication =
            await authenticator
                .AuthenticateAsync(
                    CancellationToken.None);

        Assert.Equal(
            "new-access",
            authentication.AccessToken);
        Assert.Equal(
            "capture-account",
            authentication.AccountId);
        Assert.NotNull(
            authentication.RefreshProvider);
        Assert.Equal(
            "CaptureAuthentication(redacted)",
            authentication.ToString());
        Assert.Single(handler.Requests);
        Assert.Contains(
            "new-refresh",
            await File.ReadAllTextAsync(
                credentialPath));
    }

    [Fact]
    public async Task EpicAuthenticatorRejectsMissingCredentialsAndTimeout()
    {
        var options = CreateOptions();
        using var noCredentials =
            new EnvironmentVariableScope(
                new Dictionary<string, string?>
                {
                    ["EPIC_CLIENT_ID"] = null,
                    ["EPIC_CLIENT_SECRET"] = null,
                });
        using var unusedClient =
            new HttpClient(
                new MockHttpMessageHandler());
        var missing =
            new EpicCaptureAuthenticator(
                unusedClient,
                options,
                NullLoggerFactory.Instance);
        var missingException =
            await Assert.ThrowsAsync<
                CaptureOnlyException>(() =>
                missing.AuthenticateAsync(
                    CancellationToken.None));
        Assert.Equal(
            CaptureOnlyExitCode.AuthenticationFailed,
            missingException.ExitCode);

        using var directory =
            new CaptureTestDirectory(
                "epic-auth-timeout");
        options.DeviceAuthPath = Path.Combine(
            directory.Path,
            "device-auth.json");
        await File.WriteAllTextAsync(
            options.DeviceAuthPath,
            JsonSerializer.Serialize(
                new StoredCredentials
                {
                    AccountId =
                        "capture-account",
                    RefreshToken =
                        "stored-refresh",
                }));
        var timeoutHandler =
            new MockHttpMessageHandler();
        timeoutHandler.EnqueueHang();
        using var timeoutClient =
            new HttpClient(timeoutHandler)
            {
                Timeout =
                    TimeSpan.FromMilliseconds(50),
            };
        using var credentials =
            new EnvironmentVariableScope(
                new Dictionary<string, string?>
                {
                    ["EPIC_CLIENT_ID"] =
                        "client-id",
                    ["EPIC_CLIENT_SECRET"] =
                        "client-secret",
                });
        var timedOut =
            new EpicCaptureAuthenticator(
                timeoutClient,
                options,
                NullLoggerFactory.Instance);

        var timeoutException =
            await Assert.ThrowsAsync<
                CaptureOnlyException>(() =>
                timedOut.AuthenticateAsync(
                    CancellationToken.None));
        Assert.Equal(
            CaptureOnlyExitCode.AuthenticationFailed,
            timeoutException.ExitCode);
    }

    [Fact]
    public async Task PaginationMaximumsAreCanonicalAndCatalogBound()
    {
        using var directory =
            new CaptureTestDirectory(
                "pagination-maximums");
        var providerHash =
            Hash("provider-catalog");
        var catalog = CreateCatalog(
            providerHash: providerHash);
        var document =
            new CapturePaginationMaximumsDocument(
                CapturePaginationMaximums.FormatId,
                CapturePaginationMaximums.Version,
                providerHash,
                catalog.Songs
                    .Select(song =>
                        new CapturePaginationSongMaximums(
                            song.SongId,
                            CapturePackageFormat
                                .SoloInstrumentOrder
                                .Select(instrument =>
                                    new CapturePaginationMaximum(
                                        instrument,
                                        song.SongId ==
                                                "song-a" &&
                                            instrument ==
                                                "Solo_Guitar"
                                            ? 100
                                            : null))
                                .ToArray()))
                    .ToArray());
        var path = Path.Combine(
            directory.Path,
            "pagination-maxima.json");
        var bytes =
            TierZeroCanonicalJson.Serialize(document);
        await File.WriteAllBytesAsync(
            path,
            bytes);

        var snapshot =
            await CapturePaginationMaximums
                .LoadAsync(
                    path,
                    directory.Path,
                    catalog,
                    providerHash,
                    CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(
            TierZeroCanonicalJson
                .Sha256Hex(bytes),
            snapshot.ContentSha256);
        Assert.Equal(
            100,
            snapshot.Scores["song-a"]
                .MaxLeadScore);
        Assert.Null(
            snapshot.Scores["song-b"]
                .MaxLeadScore);

        var exception =
            await Assert.ThrowsAsync<
                CaptureOnlyException>(() =>
                CapturePaginationMaximums
                    .LoadAsync(
                        path,
                        directory.Path,
                        catalog,
                        Hash("wrong-provider"),
                        CancellationToken.None));
        Assert.Equal(
            CaptureOnlyExitCode.CatalogRejected,
            exception.ExitCode);
    }

    [Fact]
    public void PaginationMaximumsRequireApprovedDevice()
    {
        CapturePaginationMaximums
            .RequireApprovedDevice(
                "259:1",
                "259:1");

        var exception =
            Assert.Throws<CaptureOnlyException>(
                () => CapturePaginationMaximums
                    .RequireApprovedDevice(
                        "259:2",
                        "259:1"));
        Assert.Equal(
            CaptureOnlyExitCode.CatalogRejected,
            exception.ExitCode);
    }

    [Fact]
    public async Task ParserStrictCaptureEnvelopeIsOptIn()
    {
        const string soloJson =
            """{"page":0,"totalPages":1,"totalEntries":1,"entries":[{"percentile":1.0,"rank":1,"sessionHistory":[{"endTime":"2026-09-12T10:00:00Z","trackedStats":{"ACCURACY":1000000,"DIFFICULTY":3,"FULL_COMBO":1,"SCORE":100,"SEASON":1,"STARS_EARNED":5}}],"teamId":"player-a"}]}""";
        await using var normalStream =
            new MemoryStream(
                System.Text.Encoding.UTF8
                    .GetBytes(soloJson));
        await using var captureStream =
            new MemoryStream(
                System.Text.Encoding.UTF8
                    .GetBytes(soloJson));

        var normal =
            await GlobalLeaderboardScraper
                .ParsePageAsync(
                    normalStream,
                    CancellationToken.None);
        var capture =
            await GlobalLeaderboardScraper
                .ParsePageAsync(
                    captureStream,
                    CancellationToken.None,
                    captureProjection: true);

        Assert.NotNull(normal);
        Assert.NotNull(capture);
        Assert.Single(normal.Entries);
        Assert.Single(capture.Entries);
        Assert.Equal(1, capture.ProviderEntryCount);
        Assert.Equal(
            1,
            capture.ProviderReportedTotalEntries);
    }

    [Fact]
    public void CaptureEntryValidationRejectsInconsistentSafeDtos()
    {
        var validBand =
            new CaptureBandLeaderboardEntry(
                "member-a:member-b",
                ["member-a", "member-b"],
                200,
                BaseScore: null,
                InstrumentBonus: null,
                OverdriveBonus: null,
                1_000_000,
                IsFullCombo: true,
                5,
                3,
                1,
                1,
                1.0,
                EndTime: null,
                "0:1",
                [
                    new CaptureBandMemberEntry(
                        0,
                        "member-a",
                        0,
                        100,
                        1_000_000,
                        true,
                        5,
                        3),
                    new CaptureBandMemberEntry(
                        1,
                        "member-b",
                        1,
                        100,
                        1_000_000,
                        true,
                        5,
                        3),
                ]);
        var identity =
            ValidateCaptureEntry(
                CaptureResponseKind
                    .BandLeaderboardPage,
                "Band_Duets",
                validBand);
        Assert.Equal(
            "member-a:member-b",
            identity.Primary);
        Assert.Equal("0:1", identity.Secondary);
        Assert.Equal(1, identity.Rank);

        var invalidBands =
            new object[]
            {
                validBand with
                {
                    TeamMembers =
                        ["member-a"],
                },
                validBand with
                {
                    TeamMembers =
                        ["member-a", "member-a"],
                },
                validBand with
                {
                    TeamKey = "wrong",
                },
                validBand with
                {
                    MemberStats =
                    [
                        validBand.MemberStats[0]
                            with
                            {
                                MemberIndex = -1,
                            },
                        validBand.MemberStats[1],
                    ],
                },
                validBand with
                {
                    MemberStats =
                    [
                        validBand.MemberStats[0],
                        validBand.MemberStats[1]
                            with
                            {
                                MemberIndex = 0,
                            },
                    ],
                },
                validBand with
                {
                    MemberStats =
                    [
                        validBand.MemberStats[0],
                        validBand.MemberStats[1]
                            with
                            {
                                AccountId =
                                    "member-a",
                            },
                    ],
                },
                validBand with
                {
                    MemberStats =
                    [
                        validBand.MemberStats[0]
                            with
                            {
                                InstrumentId = 999,
                            },
                        validBand.MemberStats[1],
                    ],
                },
                validBand with
                {
                    MemberStats =
                        [validBand.MemberStats[0]],
                },
                validBand with
                {
                    MemberStats = [],
                },
                validBand with
                {
                    InstrumentCombo = "1:2",
                },
                validBand with
                {
                    Rank = 0,
                },
                validBand with
                {
                    EndTime = "",
                },
            };
        foreach (var invalidBand in
                 invalidBands)
        {
            Assert.Throws<
                CapturePackageException>(() =>
                    ValidateCaptureEntry(
                        CaptureResponseKind
                            .BandLeaderboardPage,
                        "Band_Duets",
                        invalidBand));
        }

        var validSolo =
            new CaptureSoloLeaderboardEntry(
                "player-a",
                1,
                1.0,
                100,
                1_000_000,
                true,
                5,
                1,
                3,
                EndTime: null);
        Assert.Throws<
            CapturePackageException>(() =>
                ValidateCaptureEntry(
                    CaptureResponseKind
                        .SoloLeaderboardPage,
                    "Solo_Guitar",
                    validSolo with
                    {
                        Rank = 0,
                    }));
        Assert.Throws<
            CapturePackageException>(() =>
                ValidateCaptureEntry(
                    (CaptureResponseKind)999,
                    "Solo_Guitar",
                    validSolo));
        Assert.Throws<
            CapturePackageException>(() =>
                CaptureEntryContracts
                    .ValidateAndIdentify(
                        CaptureResponseKind
                            .SoloLeaderboardPage,
                        "Solo_Guitar",
                        JsonSerializer
                            .SerializeToElement(1),
                        RequireSafeCaptureText,
                        ThrowCaptureEntryFailure));
    }

    [Fact]
    public void CaptureEntryValidationCanCollectAllFailureKinds()
    {
        var failures =
            new List<CapturePackageFailureKind>();
        void Record(
            CapturePackageFailureKind kind,
            string message) =>
            failures.Add(kind);
        static void AcceptText(
            string? value,
            string description)
        {
        }

        _ = CaptureEntryContracts
            .ValidateAndIdentify(
                (CaptureResponseKind)999,
                "Solo_Guitar",
                JsonSerializer.SerializeToElement(
                    new { }),
                AcceptText,
                Record);
        _ = CaptureEntryContracts
            .ValidateAndIdentify(
                CaptureResponseKind
                    .SoloLeaderboardPage,
                "Solo_Guitar",
                JsonSerializer.SerializeToElement<
                    object?>(null),
                AcceptText,
                Record);
        _ = CaptureEntryContracts
            .ValidateAndIdentify(
                CaptureResponseKind
                    .BandLeaderboardPage,
                "Band_Duets",
                JsonSerializer.SerializeToElement(
                    new { }),
                AcceptText,
                Record);

        var emptyMembers =
            new CaptureBandLeaderboardEntry(
                "member-a:member-b",
                ["member-a", "member-b"],
                200,
                BaseScore: null,
                InstrumentBonus: null,
                OverdriveBonus: null,
                1_000_000,
                IsFullCombo: true,
                5,
                3,
                1,
                1,
                1.0,
                EndTime: null,
                "",
                []);
        _ = ValidateCaptureEntry(
            CaptureResponseKind
                .BandLeaderboardPage,
            "Band_Duets",
            emptyMembers,
            AcceptText,
            Record);

        var soloWithUnknownField =
            JsonDocument.Parse(
                """
                {
                  "accountId": "player-a",
                  "rank": 1,
                  "percentile": 1.0,
                  "score": 100,
                  "accuracy": 1000000,
                  "isFullCombo": true,
                  "stars": 5,
                  "season": 1,
                  "difficulty": 3,
                  "endTime": null,
                  "unknown": true
                }
                """)
                .RootElement
                .Clone();
        _ = CaptureEntryContracts
            .ValidateAndIdentify(
                CaptureResponseKind
                    .SoloLeaderboardPage,
                "Solo_Guitar",
                soloWithUnknownField,
                AcceptText,
                Record);
        var reorderedSolo =
            JsonDocument.Parse(
                """
                {
                  "rank": 1,
                  "accountId": "player-a",
                  "percentile": 1.0,
                  "score": 100,
                  "accuracy": 1000000,
                  "isFullCombo": true,
                  "stars": 5,
                  "season": 1,
                  "difficulty": 3,
                  "endTime": null
                }
                """)
                .RootElement
                .Clone();
        _ = CaptureEntryContracts
            .ValidateAndIdentify(
                CaptureResponseKind
                    .SoloLeaderboardPage,
                "Solo_Guitar",
                reorderedSolo,
                AcceptText,
                Record);

        Assert.True(failures.Count >= 7);
        Assert.All(
            failures,
            static failure =>
                Assert.Equal(
                    CapturePackageFailureKind
                        .InvalidMetadata,
                    failure));
    }

    public static TheoryData<string> MalformedCaptureEnvelopes =>
        new()
        {
            "{}",
            """{"totalPages":1,"totalEntries":1,"entries":[]}""",
            """{"page":0,"totalEntries":1,"entries":[]}""",
            """{"page":0,"totalPages":1,"entries":[]}""",
            """{"page":0,"totalPages":1,"totalEntries":1}""",
            """{"page":"0","totalPages":1,"totalEntries":1,"entries":[]}""",
            """{"page":0,"totalPages":"1","totalEntries":1,"entries":[]}""",
            """{"page":0,"totalPages":1,"totalEntries":"1","entries":[]}""",
            """{"page":0,"totalPages":1,"totalEntries":1,"entries":{}}""",
            """{"Page":0,"totalPages":1,"totalEntries":1,"entries":[]}""",
        };

    [Theory]
    [MemberData(nameof(MalformedCaptureEnvelopes))]
    public async Task CaptureParsersRejectMalformedSuccessEnvelopes(
        string json)
    {
        var bytes =
            System.Text.Encoding.UTF8.GetBytes(json);
        await using var soloStream =
            new MemoryStream(bytes);
        await using var bandStream =
            new MemoryStream(bytes);

        Assert.Null(
            await GlobalLeaderboardScraper
                .ParsePageAsync(
                    soloStream,
                    CancellationToken.None,
                    captureProjection: true));
        Assert.Null(
            await GlobalLeaderboardScraper
                .ParseBandPageAsync(
                    bandStream,
                    CancellationToken.None,
                    captureProjection: true,
                    captureBandType: "Band_Duets"));
    }

    [Fact]
    public async Task CaptureSoloParserRejectsMissingProjectedStatistics()
    {
        const string json =
            """{"page":0,"totalPages":1,"totalEntries":1,"entries":[{"percentile":1.0,"rank":1,"sessionHistory":[{"trackedStats":{"SCORE":100}}],"teamId":"player-a"}]}""";
        var bytes =
            System.Text.Encoding.UTF8.GetBytes(json);
        await using var normalStream =
            new MemoryStream(bytes);
        await using var captureStream =
            new MemoryStream(bytes);

        Assert.NotNull(
            await GlobalLeaderboardScraper
                .ParsePageAsync(
                    normalStream,
                    CancellationToken.None));
        Assert.Null(
            await GlobalLeaderboardScraper
                .ParsePageAsync(
                    captureStream,
                    CancellationToken.None,
                    captureProjection: true));
    }

    [Fact]
    public async Task BandParserRejectsLossyTeamAndInstrumentProjection()
    {
        const string invalidTeamJson =
            """
            {
              "page": 0,
              "totalPages": 1,
              "totalEntries": 1,
              "entries": [
                {
                  "teamAccountIds": ["a", "b", 42],
                  "rank": 1,
                  "sessionHistory": [{
                    "trackedStats": {
                      "SCORE": 100,
                      "M_0_ID_a": 0,
                      "M_0_INSTRUMENT": 0,
                      "M_1_ID_b": 1,
                      "M_1_INSTRUMENT": 1
                    }
                  }]
                }
              ]
            }
            """;
        await using var invalidTeamStream =
            new MemoryStream(
                System.Text.Encoding.UTF8
                    .GetBytes(invalidTeamJson));

        Assert.Null(
            await GlobalLeaderboardScraper
                .ParseBandPageAsync(
                    invalidTeamStream,
                    CancellationToken.None,
                    captureProjection: true,
                    captureBandType: "Band_Duets"));

        const string missingInstrumentJson =
            """
            {
              "page": 0,
              "totalPages": 1,
              "totalEntries": 1,
              "entries": [{
                "teamAccountIds": ["a", "b"],
                "rank": 1,
                "sessionHistory": [{
                  "trackedStats": {
                    "SCORE": 100,
                    "M_0_ID_a": 0,
                    "M_0_INSTRUMENT": 0,
                    "M_1_ID_b": 1
                  }
                }]
              }]
            }
            """;
        await using var missingInstrumentStream =
            new MemoryStream(
                System.Text.Encoding.UTF8
                    .GetBytes(missingInstrumentJson));
        Assert.Null(
            await GlobalLeaderboardScraper
                .ParseBandPageAsync(
                    missingInstrumentStream,
                    CancellationToken.None,
                    captureProjection: true,
                    captureBandType: "Band_Duets"));
    }

    [Fact]
    public async Task BandParserAcceptsCompleteTypedCaptureProjection()
    {
        const string json =
            """
            {
              "page": 0,
              "totalPages": 1,
              "totalEntries": 1,
              "entries": [{
                "teamAccountIds": ["a", "b"],
                "rank": 1,
                "percentile": 1.0,
                "sessionHistory": [{
                  "endTime": "2026-09-12T10:00:00Z",
                  "trackedStats": {
                    "SCORE": 100,
                    "ACCURACY": 1000000,
                    "FULL_COMBO": 1,
                    "STARS_EARNED": 5,
                    "SEASON": 1,
                    "DIFFICULTY": 3,
                    "M_0_ID_a": 0,
                    "M_0_INSTRUMENT": 0,
                    "M_0_SCORE": 50,
                    "M_0_ACCURACY": 1000000,
                    "M_0_FULL_COMBO": 1,
                    "M_0_STARS_EARNED": 5,
                    "M_0_DIFFICULTY": 3,
                    "M_1_ID_b": 1,
                    "M_1_INSTRUMENT": 1,
                    "M_1_SCORE": 50,
                    "M_1_ACCURACY": 1000000,
                    "M_1_FULL_COMBO": 1,
                    "M_1_STARS_EARNED": 5,
                    "M_1_DIFFICULTY": 3
                  }
                }]
              }]
            }
            """;
        await using var stream =
            new MemoryStream(
                System.Text.Encoding.UTF8
                    .GetBytes(json));

        var parsed =
            await GlobalLeaderboardScraper
                .ParseBandPageAsync(
                    stream,
                    CancellationToken.None,
                    captureProjection: true,
                    captureBandType: "Band_Duets");

        Assert.NotNull(parsed);
        Assert.Single(parsed.Entries);
        Assert.Equal(2, parsed.Entries[0].MemberStats.Count);
    }

    [Fact]
    public async Task LivePageSourceUsesSharedFetcherAndReportsWireCount()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueJsonOk(
            """
            {
              "page": 0,
              "totalPages": 1,
              "totalEntries": 1,
              "entries": [
                {
                  "percentile": 1.0,
                  "rank": 1,
                  "sessionHistory": [{
                    "endTime": "2026-09-12T10:00:00Z",
                    "trackedStats": {
                      "ACCURACY": 1000000,
                      "DIFFICULTY": 3,
                      "FULL_COMBO": 1,
                      "SCORE": 100,
                      "SEASON": 1,
                      "STARS_EARNED": 5
                    }
                  }],
                  "teamId": "player-a",
                  "token": "secret-token-value",
                  "password": "secret-password",
                  "apiKey": "secret-api-key",
                  "host": "internal.example.invalid",
                  "endpointUrl": "https://internal.example.invalid/private"
                }
              ]
            }
            """);
        using var client = new HttpClient(handler);
        var traffic = new EpicTrafficCoordinator();
        var scraper = new GlobalLeaderboardScraper(
            client,
            new ScrapeProgressTracker(),
            NullLogger<GlobalLeaderboardScraper>.Instance,
            maxLookupRetries: 0,
            festivalService: null,
            trafficCoordinator: traffic);
        using var limiter =
            new FortniteFestival.Core.Scraping
                .AdaptiveConcurrencyLimiter(
                    1,
                    1,
                    1,
                    NullLogger.Instance);
        var source = new EpicCapturePageSource(
            scraper,
            limiter);

        var result = await source.FetchAsync(
            new CaptureAuthentication(
                "secret-access-token",
                "capture-caller-account"),
            CaptureScopeKind.Solo,
            "song-a",
            "Solo_Guitar",
            0,
            CancellationToken.None);

        Assert.Equal(
            CapturePageAcquisitionStatus.Success,
            result.Status);
        Assert.True(result.IsExact);
        Assert.Equal(1, result.WireRequestCount);
        Assert.Equal(1, result.ProviderReportedTotalEntries);
        Assert.Single(result.Entries);
        var projected =
            result.Entries[0].GetRawText();
        Assert.DoesNotContain(
            "token",
            projected,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "password",
            projected,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "apiKey",
            projected,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "host",
            projected,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "endpoint",
            projected,
            StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ConcurrentLivePagesKeepIndependentWireCounts()
    {
        using var client =
            new HttpClient(
                new ConcurrentCapturePageHandler());
        var scraper = new GlobalLeaderboardScraper(
            client,
            new ScrapeProgressTracker(),
            NullLogger<GlobalLeaderboardScraper>.Instance,
            maxLookupRetries: 0);
        using var limiter =
            new FortniteFestival.Core.Scraping
                .AdaptiveConcurrencyLimiter(
                    2,
                    1,
                    2,
                    NullLogger.Instance);
        var source =
            new EpicCapturePageSource(
                scraper,
                limiter);
        var authentication =
            new CaptureAuthentication(
                "secret-access-token",
                "capture-caller-account");

        var results = await Task.WhenAll(
            source.FetchAsync(
                authentication,
                CaptureScopeKind.Solo,
                "song-a",
                "Solo_Guitar",
                0,
                CancellationToken.None),
            source.FetchAsync(
                authentication,
                CaptureScopeKind.Solo,
                "song-a",
                "Solo_Guitar",
                1,
                CancellationToken.None));

        Assert.All(
            results,
            static result =>
                Assert.Equal(
                    1,
                    result.WireRequestCount));
        Assert.Equal(
            2,
            scraper.Executor.TotalHttpSends);
    }

    [Fact]
    public async Task LivePageSourceBoundsPersistentTransportFailure()
    {
        var handler =
            new MockHttpMessageHandler();
        handler.EnqueueHang();
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var scraper = new GlobalLeaderboardScraper(
            client,
            new ScrapeProgressTracker(),
            NullLogger<
                GlobalLeaderboardScraper>.Instance,
            maxLookupRetries: 0);
        using var limiter =
            new FortniteFestival.Core.Scraping
                .AdaptiveConcurrencyLimiter(
                    1,
                    1,
                    1,
                    NullLogger.Instance);
        var source =
            new EpicCapturePageSource(
                scraper,
                limiter,
                TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<
            HttpRequestException>(() =>
            source.FetchAsync(
                new CaptureAuthentication(
                    "secret-access-token",
                    "capture-caller-account"),
                CaptureScopeKind.Solo,
                "song-a",
                "Solo_Guitar",
                0,
                CancellationToken.None));

        Assert.Single(handler.Requests);
        Assert.Equal(
            1,
            source.TotalWireRequestCount);
    }

    [Fact]
    public async Task LiveBandPageSourceProjectsExactResponse()
    {
        var handler =
            new MockHttpMessageHandler();
        handler.EnqueueJsonOk(
            """
            {
              "page": 0,
              "totalPages": 1,
              "totalEntries": 1,
              "entries": [{
                "rank": 1,
                "percentile": 1.0,
                "teamAccountIds": ["member-a", "member-b"],
                "sessionHistory": [{
                  "endTime": "2026-09-13T10:00:00Z",
                  "trackedStats": {
                    "SCORE": 200,
                    "ACCURACY": 1000000,
                    "FULL_COMBO": 1,
                    "STARS_EARNED": 5,
                    "SEASON": 1,
                    "DIFFICULTY": 3,
                    "M_0_ID_member-a": 1,
                    "M_0_INSTRUMENT": 0,
                    "M_0_SCORE": 100,
                    "M_0_ACCURACY": 1000000,
                    "M_0_FULL_COMBO": 1,
                    "M_0_STARS_EARNED": 5,
                    "M_0_DIFFICULTY": 3,
                    "M_1_ID_member-b": 1,
                    "M_1_INSTRUMENT": 1,
                    "M_1_SCORE": 100,
                    "M_1_ACCURACY": 1000000,
                    "M_1_FULL_COMBO": 1,
                    "M_1_STARS_EARNED": 5,
                    "M_1_DIFFICULTY": 3
                  }
                }]
              }]
            }
            """);
        using var client =
            new HttpClient(handler);
        var scraper =
            new GlobalLeaderboardScraper(
                client,
                new ScrapeProgressTracker(),
                NullLogger<
                    GlobalLeaderboardScraper>.Instance,
                maxLookupRetries: 0);
        using var limiter =
            new FortniteFestival.Core.Scraping
                .AdaptiveConcurrencyLimiter(
                    1,
                    1,
                    1,
                    NullLogger.Instance);
        var source =
            new EpicCapturePageSource(
                scraper,
                limiter);

        var result = await source.FetchAsync(
            new CaptureAuthentication(
                "secret-access-token",
                "capture-caller-account"),
            CaptureScopeKind.Band,
            "song-a",
            "Band_Duets",
            0,
            CancellationToken.None);

        Assert.Equal(
            CapturePageAcquisitionStatus.Success,
            result.Status);
        Assert.True(result.IsExact);
        Assert.Equal(25, result.PageSize);
        Assert.Equal(1, result.WireRequestCount);
        Assert.Single(result.Entries);
    }

    [Fact]
    public async Task LivePageSourceMapsUnauthorizedResponse()
    {
        var handler =
            new MockHttpMessageHandler();
        handler.EnqueueError(
            System.Net.HttpStatusCode
                .Unauthorized,
            """{"error":"unauthorized"}""");
        using var client =
            new HttpClient(handler);
        var scraper =
            new GlobalLeaderboardScraper(
                client,
                new ScrapeProgressTracker(),
                NullLogger<
                    GlobalLeaderboardScraper>.Instance,
                maxLookupRetries: 0);
        using var limiter =
            new FortniteFestival.Core.Scraping
                .AdaptiveConcurrencyLimiter(
                    1,
                    1,
                    1,
                    NullLogger.Instance);
        var source =
            new EpicCapturePageSource(
                scraper,
                limiter);

        var result = await source.FetchAsync(
            new CaptureAuthentication(
                "secret-access-token",
                "capture-caller-account"),
            CaptureScopeKind.Solo,
            "song-a",
            "Solo_Guitar",
            0,
            CancellationToken.None);

        Assert.Equal(
            CapturePageAcquisitionStatus.Unauthorized,
            result.Status);
        Assert.False(result.IsExact);
        Assert.Equal(1, result.WireRequestCount);
    }

    [Fact]
    public async Task LivePageSourceMarksEventNotFoundSeparately()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueJsonResponse(
            System.Net.HttpStatusCode.NotFound,
            """
            {
              "errorCode": "com.epicgames.events.event_not_found",
              "errorMessage": "Event not found"
            }
            """);
        using var client = new HttpClient(handler);
        var scraper = new GlobalLeaderboardScraper(
            client,
            new ScrapeProgressTracker(),
            NullLogger<GlobalLeaderboardScraper>.Instance,
            maxLookupRetries: 0);
        using var limiter =
            new FortniteFestival.Core.Scraping
                .AdaptiveConcurrencyLimiter(
                    1,
                    1,
                    1,
                    NullLogger.Instance);
        var source =
            new EpicCapturePageSource(
                scraper,
                limiter);

        var result = await source.FetchAsync(
            new CaptureAuthentication(
                "secret-access-token",
                "capture-caller-account"),
            CaptureScopeKind.Solo,
            "song-a",
            "Solo_Guitar",
            0,
            CancellationToken.None);

        Assert.Equal(
            CapturePageAcquisitionStatus.EventNotFound,
            result.Status);
        Assert.True(result.IsExact);
        Assert.Equal(0, result.ProviderReportedTotalPages);
        Assert.Equal(0, result.ProviderReportedTotalEntries);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task LivePageSourceKeepsHttpSuccessEmptyDistinct()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueJsonOk(
            """
            {
              "page": 0,
              "totalPages": 0,
              "totalEntries": 0,
              "entries": []
            }
            """);
        using var client = new HttpClient(handler);
        var scraper = new GlobalLeaderboardScraper(
            client,
            new ScrapeProgressTracker(),
            NullLogger<GlobalLeaderboardScraper>.Instance,
            maxLookupRetries: 0);
        using var limiter =
            new FortniteFestival.Core.Scraping
                .AdaptiveConcurrencyLimiter(
                    1,
                    1,
                    1,
                    NullLogger.Instance);
        var source =
            new EpicCapturePageSource(
                scraper,
                limiter);

        var result = await source.FetchAsync(
            new CaptureAuthentication(
                "secret-access-token",
                "capture-caller-account"),
            CaptureScopeKind.Solo,
            "song-a",
            "Solo_Guitar",
            0,
            CancellationToken.None);

        Assert.Equal(
            CapturePageAcquisitionStatus.Success,
            result.Status);
        Assert.True(result.IsExact);
        Assert.Equal(0, result.ProviderReportedTotalPages);
        Assert.Equal(0, result.ProviderReportedTotalEntries);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task CaptureDoesNotTreatMalformed404AsEventNotFound()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueJsonResponse(
            System.Net.HttpStatusCode.NotFound,
            "prefix com.epicgames.events.event_not_found suffix");
        using var client = new HttpClient(handler);
        var scraper = new GlobalLeaderboardScraper(
            client,
            new ScrapeProgressTracker(),
            NullLogger<GlobalLeaderboardScraper>.Instance,
            maxLookupRetries: 0);
        using var limiter =
            new FortniteFestival.Core.Scraping
                .AdaptiveConcurrencyLimiter(
                    1,
                    1,
                    1,
                    NullLogger.Instance);
        var source =
            new EpicCapturePageSource(
                scraper,
                limiter);

        var result = await source.FetchAsync(
            new CaptureAuthentication(
                "secret-access-token",
                "capture-caller-account"),
            CaptureScopeKind.Solo,
            "song-a",
            "Solo_Guitar",
            0,
            CancellationToken.None);

        Assert.Equal(
            CapturePageAcquisitionStatus.RequestFailure,
            result.Status);
        Assert.False(result.IsExact);
    }

    [Fact]
    public void CaptureCompositionHasNoDatabasePublicationOrHostedServices()
    {
        using var directory =
            new CaptureTestDirectory(
                "composition");
        AssertCaptureFailure(
            CaptureOnlyExitCode.Usage,
            () => CaptureOnlyComposition
                .CreateServiceCollection(
                    CreateEnvironment(
                        directory.Path),
                    CreateOptions()));
        var options = CreateOptions();
        options.ProxyCurlTempDirectory =
            Path.Combine(
                directory.Path,
                CaptureScratchPath.DirectoryName);
        var services =
            CaptureOnlyComposition
                .CreateServiceCollection(
                    CreateEnvironment(
                        directory.Path),
                    options);
        var forbidden = new[]
        {
            typeof(NpgsqlDataSource),
            typeof(IMetaDatabase),
            typeof(NotificationService),
            typeof(ScraperWorker),
            typeof(IHostedService),
            typeof(IPathDataStore),
            typeof(WorkerStatusPublisher),
            typeof(ScrapeOrchestrator),
            typeof(ScrapeLifecycleNotifier),
        };

        foreach (var type in forbidden)
        {
            Assert.DoesNotContain(
                services,
                descriptor =>
                    type.IsAssignableFrom(
                        descriptor.ServiceType) ||
                    descriptor.ImplementationType is
                        not null &&
                    type.IsAssignableFrom(
                        descriptor
                            .ImplementationType));
        }
        Assert.DoesNotContain(
            services,
            descriptor =>
                descriptor.ServiceType.Namespace?
                    .StartsWith(
                        "FSTService.Persistence",
                        StringComparison.Ordinal) ==
                true);

        using var provider =
            services.BuildServiceProvider(
                new ServiceProviderOptions
                {
                    ValidateOnBuild = true,
                    ValidateScopes = true,
                });
        var scraper =
            provider.GetRequiredService<
                GlobalLeaderboardScraper>();
        Assert.Equal(
            options.ProxyCurlTempDirectory,
            scraper.Executor
                .CurlFallbackTempDirectory);
        Assert.Equal(
            CapturePackageFormat
                .MaximumResponseRecordBytes,
            scraper.Executor
                .CurlResponseMaximumBytes);
        Assert.IsType<CaptureOnlyRunner>(
            provider.GetRequiredService<
                ICaptureOnlyRunner>());
    }

    [Fact]
    public void CaptureCompositionRejectsNoScopesAndInvalidProxyTopology()
    {
        using var directory =
            new CaptureTestDirectory(
                "composition-invalid-options");
        var environment =
            CreateEnvironment(directory.Path);
        var noScopes = CreateOptions(
            enableBand: false,
            enableSolo: false);
        AssertCaptureFailure(
            CaptureOnlyExitCode.Usage,
            () => CaptureOnlyComposition
                .CreateServiceCollection(
                    environment,
                    noScopes));

        var invalidProxy =
            CreateOptions(
                enableBand: false);
        invalidProxy.ProxyUrls =
            ["http://proxy.invalid:8888"];
        invalidProxy.ControlUrls = [];
        invalidProxy.VpnProviders = [];
        invalidProxy.ContainerNames = [];
        AssertCaptureFailure(
            CaptureOnlyExitCode.Usage,
            () => CaptureOnlyComposition
                .CreateServiceCollection(
                    environment,
                    invalidProxy));
    }

    [Fact]
    public async Task CaptureHttpClientRejectsOversizedBufferedResponse()
    {
        var inner = new MockHttpMessageHandler();
        inner.EnqueueJsonOk("""{"tooLarge":true}""");
        using var client = new HttpClient(
            new CaptureResponseLimitHandler(
                inner,
                maximumBytes: 4));

        await Assert.ThrowsAsync<
            ResponseBodyLimitExceededException>(
            () => client.GetAsync(
                "https://example.invalid"));
    }

    [Fact]
    public async Task CaptureHttpClientRejectsOversizedStreamingResponse()
    {
        var inner = new MockHttpMessageHandler();
        inner.EnqueueResponse(
            new HttpResponseMessage(
                System.Net.HttpStatusCode.OK)
            {
                Content = new UnknownLengthContent(
                    "streaming-response"u8
                        .ToArray()),
            });
        using var client = new HttpClient(
            new CaptureResponseLimitHandler(
                inner,
                maximumBytes: 4));

        await Assert.ThrowsAsync<
            ResponseBodyLimitExceededException>(
            () => client.GetAsync(
                "https://example.invalid"));
    }

    [Fact]
    public async Task CaptureHttpClientBuffersAllowedResponseAndValidatesLimit()
    {
        Assert.Throws<
            ArgumentOutOfRangeException>(() =>
                new CaptureResponseLimitHandler(
                    new MockHttpMessageHandler(),
                    0));
        Assert.Throws<
            ArgumentOutOfRangeException>(() =>
                new CaptureResponseLimitHandler(
                    new MockHttpMessageHandler(),
                    (long)int.MaxValue + 1));

        var inner =
            new MockHttpMessageHandler();
        var content =
            new UnknownLengthContent(
                "small-response"u8
                    .ToArray());
        content.Headers.ContentType =
            new System.Net.Http.Headers
                .MediaTypeHeaderValue(
                    "application/json");
        inner.EnqueueResponse(
            new HttpResponseMessage(
                System.Net.HttpStatusCode.OK)
            {
                Content = content,
            });
        using var client =
            new HttpClient(
                new CaptureResponseLimitHandler(
                    inner,
                    maximumBytes: 64));

        Assert.Equal(
            "small-response",
            await client.GetStringAsync(
                "https://example.invalid"));
    }

    [Fact]
    public void ScratchPathRequiresExactNonOverlappingLocation()
    {
        using var directory =
            new CaptureTestDirectory(
                "scratch-path");
        var scratch = Path.Combine(
            directory.Path,
            CaptureScratchPath.DirectoryName);

        AssertCaptureFailure(
            CaptureOnlyExitCode.Usage,
            () => CaptureScratchPath.Validate(
                "",
                directory.Path));
        AssertCaptureFailure(
            CaptureOnlyExitCode.Usage,
            () => CaptureScratchPath.Validate(
                "relative",
                directory.Path));
        AssertCaptureFailure(
            CaptureOnlyExitCode.Usage,
            () => CaptureScratchPath.Validate(
                Path.Combine(
                    directory.Path,
                    "other"),
                directory.Path));

        File.WriteAllText(scratch, "");
        AssertCaptureFailure(
            CaptureOnlyExitCode.Usage,
            () => CaptureScratchPath.Validate(
                scratch,
                directory.Path));
        File.Delete(scratch);
        Directory.CreateDirectory(scratch);

        Assert.Equal(
            scratch,
            CaptureScratchPath.Validate(
                scratch,
                directory.Path));
        AssertCaptureFailure(
            CaptureOnlyExitCode.Usage,
            () => CaptureScratchPath.Validate(
                scratch,
                directory.Path,
                scratch));
        AssertCaptureFailure(
            CaptureOnlyExitCode.Usage,
            () => CaptureScratchPath.Validate(
                scratch,
                directory.Path,
                Path.Combine(
                    scratch,
                    "capture")));
        AssertCaptureFailure(
            CaptureOnlyExitCode.Usage,
            () => CaptureScratchPath.Validate(
                scratch,
                directory.Path,
                directory.Path));
    }

    [Fact]
    public async Task RunnerCapturesSoloAndBandAndRoundTrips()
    {
        using var directory =
            new CaptureTestDirectory(
                "runner-roundtrip");
        var output = Path.Combine(
            directory.Path,
            "capture-1");
        var catalog = CreateCatalog();
        var catalogSource =
            new QueueCatalogSource(
                ExactCatalog(catalog),
                ExactCatalog(catalog));
        var pageSource = new DictionaryPageSource(
            new Dictionary<
                CapturePageKey,
                CapturePageAcquisition>
            {
                [new(
                    CaptureScopeKind.Solo,
                    "song-a",
                    "Solo_Guitar",
                    0)] = Page(
                        0,
                        2,
                        2,
                        1,
                        SoloEntry(
                            "player-a",
                            1),
                        pageSize: 1),
                [new(
                    CaptureScopeKind.Solo,
                    "song-a",
                    "Solo_Guitar",
                    1)] = Page(
                        1,
                        2,
                        2,
                        2,
                        SoloEntry(
                            "player-b",
                            2),
                        pageSize: 1),
                [new(
                    CaptureScopeKind.Band,
                    "song-a",
                    "Band_Duets",
                    0)] = Page(
                        0,
                        1,
                        1,
                        1,
                        BandEntry(
                            "band-a",
                            "band-b",
                            1),
                        pageSize: 25),
            });
        var environment = CreateEnvironment(
            directory.Path);
        var runner = CreateRunner(
            environment,
            CreateOptions(),
            catalogSource,
            pageSource,
            new TestStorageProbe());

        var result = await runner.ExecuteAsync(
            new CaptureOnlyCommand(
                output,
                "capture-1"),
            CancellationToken.None);
        var package =
            await new CapturePackageReader()
                .LoadAsync(output);

        Assert.Equal(
            result.PackageRootHash,
            package.Envelope.PackageRootHash);
        Assert.Equal(
            CapturePackageFormat.FormatId,
            package.Manifest.FormatId);
        Assert.Equal(
            ["Solo_Guitar"],
            package.Manifest
                .EnabledSoloInstruments);
        Assert.Equal(
            CapturePackageFormat.BandTypeOrder,
            package.Manifest.EnabledBandTypes);
        Assert.Equal(8, package.Scopes.Count);
        Assert.Equal(3, package.Requests.Count);
        Assert.Equal(
            1,
            package.Manifest.ResponseShardCount);
        Assert.Equal(
            [
                (
                    "song-a",
                    CaptureScopeKind.Solo,
                    "Solo_Guitar",
                    0),
                (
                    "song-a",
                    CaptureScopeKind.Solo,
                    "Solo_Guitar",
                    1),
                (
                    "song-a",
                    CaptureScopeKind.Band,
                    "Band_Duets",
                    0),
            ],
            package.Requests.Select(
                static request => (
                    request.SongId,
                    request.ScopeKind,
                    request.LeaderboardType,
                    request.PageIndex)));
        Assert.Equal(
            4,
            package.Manifest.TotalRequestCount);
        Assert.Equal(
            [1L, 2L, 1L],
            package.Requests.Select(
                static request =>
                    request.CapturedRequestCount));
        Assert.DoesNotContain(
            "secret-access-token",
            ReadPackageText(output));
        Assert.DoesNotContain(
            "capture-caller-account",
            ReadPackageText(output));

        var retainedAdmission =
            new CaptureRootAdmission(
                new CaptureRootPolicyOptions(
                    directory.Path,
                    null,
                    TestOnly: true),
                new CaptureFileSystemStorageProbe());
        var next = retainedAdmission.AdmitOutput(
            Path.Combine(
                directory.Path,
                "capture-2"));
        AssertCaptureFailure(
            CaptureOnlyExitCode.AdmissionRejected,
            () => retainedAdmission.Preflight(
                next,
                environment.StoragePolicy with
                {
                    MaximumRetainedSealedPackages = 1,
                },
                CancellationToken.None));
        Assert.True(
            File.Exists(
                Path.Combine(
                    output,
                    TierZeroEvidenceFormat
                        .ManifestFileName)));
    }

    [Fact]
    public async Task RunnerUsesConfiguredSoloPageLimit()
    {
        using var directory =
            new CaptureTestDirectory(
                "solo-page-limit");
        var catalog = CreateCatalog(
            includeBand: false);
        var pageSource = new DictionaryPageSource(
            new Dictionary<
                CapturePageKey,
                CapturePageAcquisition>
            {
                [new(
                    CaptureScopeKind.Solo,
                    "song-a",
                    "Solo_Guitar",
                    0)] = Page(
                        0,
                        3,
                        3,
                        1,
                        SoloEntry("player-a", 1),
                        pageSize: 1),
                [new(
                    CaptureScopeKind.Solo,
                    "song-a",
                    "Solo_Guitar",
                    1)] = Page(
                        1,
                        3,
                        3,
                        1,
                        SoloEntry("player-b", 2),
                        pageSize: 1),
            });
        var options = CreateOptions(
            enableBand: false);
        options.MaxPagesPerLeaderboard = 2;
        var catalogResult = ExactCatalog(
            catalog,
            maximumScores:
                new Dictionary<string, SongMaxScores>(
                    StringComparer.Ordinal)
                {
                    ["song-a"] =
                        new SongMaxScores(),
                });
        var runner = CreateRunner(
            CreateEnvironment(directory.Path),
            options,
            new QueueCatalogSource(
                catalogResult,
                catalogResult),
            pageSource,
            new TestStorageProbe());

        await runner.ExecuteAsync(
            new CaptureOnlyCommand(
                Path.Combine(
                    directory.Path,
                    "capture"),
                "capture-solo-limit"),
            CancellationToken.None);
        var package =
            await new CapturePackageReader()
                .LoadAsync(
                    Path.Combine(
                        directory.Path,
                        "capture"));
        var scope = Assert.Single(
            package.Scopes,
            static scope =>
                scope.Status ==
                CaptureScopeStatus.Complete);

        Assert.Equal(2, package.Requests.Count);
        Assert.Equal(
            [0, 1],
            package.Requests.Select(
                static request =>
                    request.PageIndex));
        Assert.Equal(
            CaptureScopeCompletionReason
                .ConfiguredPageLimit,
            scope.CompletionReason);
        Assert.Equal(3, scope.ProviderReportedTotalEntries);
        Assert.Equal(2, scope.CapturedEntryCount);
        Assert.DoesNotContain(
            pageSource.RequestedKeys,
            static key => key.PageIndex == 2);
    }

    [Fact]
    public async Task RunnerRejectsAmbiguousTruncationWithoutMaximumSnapshot()
    {
        using var directory =
            new CaptureTestDirectory(
                "missing-pagination-maxima");
        var catalog = CreateCatalog(
            includeBand: false);
        var pageSource = new DictionaryPageSource(
            new Dictionary<
                CapturePageKey,
                CapturePageAcquisition>
            {
                [new(
                    CaptureScopeKind.Solo,
                    "song-a",
                    "Solo_Guitar",
                    0)] = Page(
                        0,
                        3,
                        3,
                        1,
                        SoloEntry("player-a", 1),
                        pageSize: 1),
            });
        var options = CreateOptions(
            enableBand: false);
        options.MaxPagesPerLeaderboard = 2;
        var output = Path.Combine(
            directory.Path,
            "capture");
        var runner = CreateRunner(
            CreateEnvironment(directory.Path),
            options,
            new QueueCatalogSource(
                ExactCatalog(catalog)),
            pageSource,
            new TestStorageProbe());

        var exception =
            await Assert.ThrowsAsync<
                CaptureOnlyException>(() =>
                runner.ExecuteAsync(
                    new CaptureOnlyCommand(
                        output,
                        "capture-missing-maxima"),
                    CancellationToken.None));

        Assert.Equal(
            CaptureOnlyExitCode.CaptureFailed,
            exception.ExitCode);
        Assert.Single(pageSource.RequestedKeys);
        AssertInterrupted(
            output,
            "capture-incomplete");
    }

    [Fact]
    public async Task RunnerUsesSoloDeepScrapeValidEntryPlan()
    {
        using var directory =
            new CaptureTestDirectory(
                "solo-deep-plan");
        var catalog = CreateCatalog(
            includeBand: false);
        var maximumScores =
            new Dictionary<string, SongMaxScores>(
                StringComparer.Ordinal)
            {
                ["song-a"] = new SongMaxScores
                {
                    MaxLeadScore = 100,
                },
            };
        var pageSource = new DelayedPageSource(
            new Dictionary<
                CapturePageKey,
                (CapturePageAcquisition Page,
                 TimeSpan Delay)>
            {
                [new(
                    CaptureScopeKind.Solo,
                    "song-a",
                    "Solo_Guitar",
                    0)] = (
                        Page(
                            0,
                            4,
                            4,
                            1,
                            SoloEntry(
                                "player-a",
                                1,
                                score: 200),
                            pageSize: 1),
                        TimeSpan.Zero),
                [new(
                    CaptureScopeKind.Solo,
                    "song-a",
                    "Solo_Guitar",
                    1)] = (
                        Page(
                            1,
                            4,
                            4,
                            1,
                            SoloEntry(
                                "player-b",
                                2,
                                score: 90),
                            pageSize: 1),
                        TimeSpan.FromMilliseconds(50)),
                [new(
                    CaptureScopeKind.Solo,
                    "song-a",
                    "Solo_Guitar",
                    2)] = (
                        Page(
                            2,
                            4,
                            4,
                            1,
                            SoloEntry(
                                "player-c",
                                3,
                                score: 80),
                            pageSize: 1),
                        TimeSpan.FromSeconds(1)),
                [new(
                    CaptureScopeKind.Solo,
                    "song-a",
                    "Solo_Guitar",
                    3)] = (
                        Page(
                            3,
                            4,
                            4,
                            1,
                            SoloEntry(
                                "player-d",
                                4,
                                score: 70),
                            pageSize: 1),
                        TimeSpan.FromMilliseconds(1)),
            });
        var options = CreateOptions(
            enableBand: false);
        options.MaxPagesPerLeaderboard = 1;
        options.OverThresholdExtraPages = 100;
        options.ValidEntryTarget = 1;
        var catalogResult = ExactCatalog(
            catalog,
            maximumScores:
                maximumScores);
        var runner = CreateRunner(
            CreateEnvironment(directory.Path),
            options,
            new QueueCatalogSource(
                catalogResult,
                catalogResult),
            pageSource,
            new TestStorageProbe());

        await runner.ExecuteAsync(
            new CaptureOnlyCommand(
                Path.Combine(
                    directory.Path,
                    "capture"),
                "capture-solo-deep"),
            CancellationToken.None);
        var package =
            await new CapturePackageReader()
                .LoadAsync(
                    Path.Combine(
                        directory.Path,
                        "capture"));
        var scope = Assert.Single(
            package.Scopes,
            static scope =>
                scope.Status ==
                CaptureScopeStatus.Complete);

        Assert.Equal(
            [0, 1],
            package.Requests.Select(
                static request =>
                    request.PageIndex));
        Assert.Equal(
            CaptureScopeCompletionReason
                .ValidEntryTargetReached,
            scope.CompletionReason);
        Assert.Equal(
            3,
            package.Requests[1]
                .CapturedRequestCount);
        Assert.Equal(
            4,
            scope.CapturedRequestCount);
        Assert.Contains(
            package.Envelope.ParentRootHashes,
            parent =>
                parent.LogicalParent ==
                    "capture-pagination-max-scores" &&
                parent.Sha256 ==
                    Hash("pagination-maximums"));
        Assert.DoesNotContain(
            2,
            pageSource.CompletionOrder);
        Assert.Contains(
            3,
            pageSource.CompletionOrder);
    }

    [Fact]
    public async Task RunnerAccountsForWireSendsCompletedDuringQuiescence()
    {
        using var directory =
            new CaptureTestDirectory(
                "quiesced-wire-sends");
        var catalog = CreateCatalog(
            includeBand: false);
        var pageSource =
            new QuiescingPageSource(
                Page(
                    0,
                    1,
                    1,
                    1,
                    SoloEntry(
                        "player-a",
                        1),
                    pageSize: 1));
        var catalogResult =
            ExactCatalog(catalog);
        var output = Path.Combine(
            directory.Path,
            "capture");
        var runner = CreateRunner(
            CreateEnvironment(directory.Path),
            CreateOptions(enableBand: false),
            new QueueCatalogSource(
                catalogResult,
                catalogResult),
            pageSource,
            new TestStorageProbe());

        await runner.ExecuteAsync(
            new CaptureOnlyCommand(
                output,
                "capture-quiesced-sends"),
            CancellationToken.None);
        var package =
            await new CapturePackageReader()
                .LoadAsync(output);
        var request = Assert.Single(
            package.Requests);
        var scope = Assert.Single(
            package.Scopes,
            static item =>
                item.Status ==
                CaptureScopeStatus.Complete);

        Assert.True(pageSource.Quiesced);
        Assert.Equal(
            2,
            request.CapturedRequestCount);
        Assert.Equal(
            2,
            scope.CapturedRequestCount);
    }

    [Fact]
    public async Task RunnerUsesLegacySoloExtensionWhenTargetDisabled()
    {
        using var directory =
            new CaptureTestDirectory(
                "legacy-solo-extension");
        var catalog = CreateCatalog(
            includeBand: false);
        var maximumScores =
            new Dictionary<string, SongMaxScores>(
                StringComparer.Ordinal)
            {
                ["song-a"] = new SongMaxScores
                {
                    MaxLeadScore = 100,
                },
            };
        var pages =
            new Dictionary<
                CapturePageKey,
                CapturePageAcquisition>();
        for (var pageIndex = 0;
             pageIndex < 3;
             pageIndex++)
        {
            pages[new CapturePageKey(
                CaptureScopeKind.Solo,
                "song-a",
                "Solo_Guitar",
                pageIndex)] = Page(
                    pageIndex,
                    4,
                    4,
                    1,
                    SoloEntry(
                        $"player-{pageIndex}",
                        pageIndex + 1,
                        score: 200 - pageIndex),
                    pageSize: 1);
        }
        var options = CreateOptions(
            enableBand: false);
        options.MaxPagesPerLeaderboard = 1;
        options.OverThresholdExtraPages = 2;
        options.ValidEntryTarget = 0;
        var catalogResult = ExactCatalog(
            catalog,
            maximumScores:
                maximumScores);
        var output = Path.Combine(
            directory.Path,
            "capture");
        var runner = CreateRunner(
            CreateEnvironment(directory.Path),
            options,
            new QueueCatalogSource(
                catalogResult,
                catalogResult),
            new DictionaryPageSource(pages),
            new TestStorageProbe());

        await runner.ExecuteAsync(
            new CaptureOnlyCommand(
                output,
                "capture-legacy-extension"),
            CancellationToken.None);
        var package =
            await new CapturePackageReader()
                .LoadAsync(output);
        var scope = Assert.Single(
            package.Scopes,
            static item =>
                item.Status ==
                CaptureScopeStatus.Complete);

        Assert.Equal(
            [0, 1, 2],
            package.Requests.Select(
                static request =>
                    request.PageIndex));
        Assert.Equal(
            CaptureScopeCompletionReason
                .ConfiguredPageLimit,
            scope.CompletionReason);
    }

    [Fact]
    public void FileSystemStorageProbeCountsAndMeasuresPackages()
    {
        using var directory =
            new CaptureTestDirectory(
                "filesystem-storage-probe");
        var sealedPackage = Path.Combine(
            directory.Path,
            "sealed");
        var incompletePackage = Path.Combine(
            directory.Path,
            "incomplete");
        Directory.CreateDirectory(
            Path.Combine(
                sealedPackage,
                "capture"));
        Directory.CreateDirectory(
            incompletePackage);
        File.WriteAllText(
            Path.Combine(
                sealedPackage,
                TierZeroEvidenceFormat
                    .ManifestFileName),
            "manifest");
        File.WriteAllText(
            Path.Combine(
                sealedPackage,
                CapturePackageFormat.ManifestPath
                    .Replace(
                        '/',
                        Path.DirectorySeparatorChar)),
            "capture");
        File.WriteAllText(
            Path.Combine(
                incompletePackage,
                "payload.bin"),
            "payload");
        var probe =
            new CaptureFileSystemStorageProbe();

        Assert.True(
            probe.GetAvailableFreeSpace(
                directory.Path) > 0);
        Assert.Equal(
            1,
            probe.CountRetainedSealedPackages(
                directory.Path,
                1,
                CancellationToken.None));
        Assert.Equal(
            7,
            probe.GetPackageBytes(
                incompletePackage,
                CancellationToken.None));
        Assert.Throws<
            ArgumentOutOfRangeException>(() =>
                probe.CountRetainedSealedPackages(
                    directory.Path,
                    0,
                    CancellationToken.None));
        using var cancellation =
            new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<
            OperationCanceledException>(() =>
                probe.CountRetainedSealedPackages(
                    directory.Path,
                    2,
                    cancellation.Token));
    }

    [Fact]
    public async Task RunnerSequentialSoloStopsAtConfiguredInitialPagePlan()
    {
        using var directory =
            new CaptureTestDirectory(
                "sequential-solo-plan");
        var catalog = CreateCatalog(
            includeBand: false);
        var pageSource = new DictionaryPageSource(
            new Dictionary<
                CapturePageKey,
                CapturePageAcquisition>
            {
                [new(
                    CaptureScopeKind.Solo,
                    "song-a",
                    "Solo_Guitar",
                    0)] = Page(
                        0,
                        3,
                        3,
                        1,
                        SoloEntry(
                            "player-a",
                            1,
                            score: 200),
                        pageSize: 1),
                [new(
                    CaptureScopeKind.Solo,
                    "song-a",
                    "Solo_Guitar",
                    1)] = Page(
                        1,
                        3,
                        3,
                        1,
                        SoloEntry(
                            "player-b",
                            2,
                            score: 90),
                        pageSize: 1),
            });
        var options = CreateOptions(
            enableBand: false);
        options.SequentialScrape = true;
        options.MaxPagesPerLeaderboard = 1;
        options.OverThresholdExtraPages = 1;
        options.ValidEntryTarget = 2;
        var catalogResult = ExactCatalog(catalog);
        var runner = CreateRunner(
            CreateEnvironment(directory.Path),
            options,
            new QueueCatalogSource(
                catalogResult,
                catalogResult),
            pageSource,
            new TestStorageProbe());

        await runner.ExecuteAsync(
            new CaptureOnlyCommand(
                Path.Combine(
                    directory.Path,
                    "capture"),
                "capture-sequential-solo"),
            CancellationToken.None);
        var package =
            await new CapturePackageReader()
                .LoadAsync(
                    Path.Combine(
                        directory.Path,
                        "capture"));
        var scope = Assert.Single(
            package.Scopes,
            static scope =>
                scope.Status ==
                CaptureScopeStatus.Complete);

        Assert.Single(package.Requests);
        Assert.Equal(
            CaptureScopeCompletionReason
                .ConfiguredPageLimit,
            scope.CompletionReason);
        Assert.DoesNotContain(
            pageSource.RequestedKeys,
            static key => key.PageIndex == 1);
    }

    [Fact]
    public async Task RunnerUsesActiveBandPageCap()
    {
        using var directory =
            new CaptureTestDirectory(
                "band-valid-target");
        var catalog = CreateCatalog(
            includeBand: true);
        var pageSource = new DictionaryPageSource(
            new Dictionary<
                CapturePageKey,
                CapturePageAcquisition>
            {
                [new(
                    CaptureScopeKind.Band,
                    "song-a",
                    "Band_Duets",
                    0)] = Page(
                        0,
                        3,
                        3,
                        1,
                        BandEntry("a", "b", 1),
                        pageSize: 1),
                [new(
                    CaptureScopeKind.Band,
                    "song-a",
                    "Band_Duets",
                    1)] = Page(
                        1,
                        3,
                        3,
                        1,
                        BandEntry("c", "d", 2),
                        pageSize: 1),
            });
        var options = CreateOptions(
            enableBand: true,
            enableSolo: false);
        options.MaxPagesPerLeaderboard = 2;
        options.BandMaxPagesPerLeaderboard = 1;
        options.BandValidEntryTarget = 1;
        var catalogResult = ExactCatalog(
            catalog,
            maximumScores:
                new Dictionary<string, SongMaxScores>(
                    StringComparer.Ordinal)
                {
                    ["song-a"] =
                        new SongMaxScores(),
                });
        var runner = CreateRunner(
            CreateEnvironment(directory.Path),
            options,
            new QueueCatalogSource(
                catalogResult,
                catalogResult),
            pageSource,
            new TestStorageProbe());

        await runner.ExecuteAsync(
            new CaptureOnlyCommand(
                Path.Combine(
                    directory.Path,
                    "capture"),
                "capture-band-target"),
            CancellationToken.None);
        var package =
            await new CapturePackageReader()
                .LoadAsync(
                    Path.Combine(
                        directory.Path,
                        "capture"));
        var scope = Assert.Single(
            package.Scopes,
            static scope =>
                scope.Status ==
                CaptureScopeStatus.Complete);

        Assert.Equal(
            [0, 1],
            package.Requests.Select(
                static request =>
                    request.PageIndex));
        Assert.Equal(
            CaptureScopeCompletionReason
                .ConfiguredPageLimit,
            scope.CompletionReason);
        Assert.DoesNotContain(
            pageSource.RequestedKeys,
            static key => key.PageIndex == 2);
    }

    [Fact]
    public async Task ConcurrentPageCompletionRetainsCanonicalOrder()
    {
        using var directory =
            new CaptureTestDirectory(
                "canonical-page-order");
        var catalog = CreateCatalog(
            includeBand: false);
        var pageSource =
            new DelayedPageSource(
                new Dictionary<
                    CapturePageKey,
                    (CapturePageAcquisition Page,
                     TimeSpan Delay)>
                {
                    [new(
                        CaptureScopeKind.Solo,
                        "song-a",
                        "Solo_Guitar",
                        0)] = (
                        Page(
                            0,
                            3,
                            3,
                            1,
                            SoloEntry("a", 1),
                            pageSize: 1),
                        TimeSpan.Zero),
                    [new(
                        CaptureScopeKind.Solo,
                        "song-a",
                        "Solo_Guitar",
                        1)] = (
                        Page(
                            1,
                            3,
                            3,
                            1,
                            SoloEntry("b", 2),
                            pageSize: 1),
                        TimeSpan.FromMilliseconds(100)),
                    [new(
                        CaptureScopeKind.Solo,
                        "song-a",
                        "Solo_Guitar",
                        2)] = (
                        Page(
                            2,
                            3,
                            3,
                            1,
                            SoloEntry("c", 3),
                            pageSize: 1),
                        TimeSpan.FromMilliseconds(5)),
                });
        var options = CreateOptions(
            enableBand: false);
        options.MaxPagesPerLeaderboard = 3;
        options.DegreeOfParallelism = 2;
        var runner = CreateRunner(
            CreateEnvironment(directory.Path),
            options,
            new QueueCatalogSource(
                ExactCatalog(catalog),
                ExactCatalog(catalog)),
            pageSource,
            new TestStorageProbe());

        await runner.ExecuteAsync(
            new CaptureOnlyCommand(
                Path.Combine(
                    directory.Path,
                    "capture"),
                "capture-page-order"),
            CancellationToken.None);
        var package =
            await new CapturePackageReader()
                .LoadAsync(
                    Path.Combine(
                        directory.Path,
                        "capture"));

        Assert.Equal(
            [2, 1],
            pageSource.CompletionOrder);
        Assert.Equal(
            [0, 1, 2],
            package.Requests.Select(
                static request =>
                    request.PageIndex));
    }

    [Fact]
    public async Task RunnerSealsExplicitEventNotFoundEmpty()
    {
        using var directory =
            new CaptureTestDirectory(
                "event-not-found");
        var catalog = CreateCatalog(
            includeBand: false);
        var output = Path.Combine(
            directory.Path,
            "capture");
        var runner = CreateRunner(
            CreateEnvironment(directory.Path),
            CreateOptions(enableBand: false),
            new QueueCatalogSource(
                ExactCatalog(catalog),
                ExactCatalog(catalog)),
            new DictionaryPageSource(
                new Dictionary<
                    CapturePageKey,
                    CapturePageAcquisition>
                {
                    [new(
                        CaptureScopeKind.Solo,
                        "song-a",
                        "Solo_Guitar",
                        0)] = new CapturePageAcquisition(
                            CapturePageAcquisitionStatus
                                .EventNotFound,
                            0,
                            100,
                            0,
                            0,
                            [],
                            1,
                            80,
                            IsExact: true),
                }),
            new TestStorageProbe());

        await runner.ExecuteAsync(
            new CaptureOnlyCommand(
                output,
                "capture-event-empty"),
            CancellationToken.None);
        var package =
            await new CapturePackageReader()
                .LoadAsync(output);
        var scope = Assert.Single(
            package.Scopes,
            static scope =>
                scope.Status ==
                CaptureScopeStatus.Complete);
        var responsePath = Path.Combine(
            output,
            package.Requests[0]
                .ResponseShardPath.Replace(
                    '/',
                    Path.DirectorySeparatorChar));
        var response =
            CapturePackageContract.DeserializeResponse(
                System.Text.Encoding.UTF8.GetBytes(
                    File.ReadLines(responsePath)
                        .Single()));

        Assert.Equal(
            CaptureScopeCompletionReason.EventNotFound,
            scope.CompletionReason);
        Assert.Equal(
            CaptureResponseOrigin.EventNotFound,
            response.Origin);
    }

    [Fact]
    public async Task AuthenticationFailureLeavesInterruptedPackage()
    {
        using var directory =
            new CaptureTestDirectory(
                "auth-failure");
        var output = Path.Combine(
            directory.Path,
            "capture");
        var runner = CreateRunner(
            CreateEnvironment(directory.Path),
            CreateOptions(),
            new QueueCatalogSource(),
            new DictionaryPageSource(
                new Dictionary<
                    CapturePageKey,
                    CapturePageAcquisition>()),
            new TestStorageProbe(),
            new ThrowingAuthenticator(
                new InvalidOperationException(
                    "secret-access-token")));

        var exception =
            await Assert.ThrowsAsync<
                CaptureOnlyException>(() =>
                runner.ExecuteAsync(
                    new CaptureOnlyCommand(
                        output,
                        "capture-auth-failure"),
                    CancellationToken.None));

        Assert.Equal(
            CaptureOnlyExitCode.AuthenticationFailed,
            exception.ExitCode);
        AssertInterrupted(
            output,
            "capture-authentication-failed");
        Assert.DoesNotContain(
            "secret-access-token",
            ReadPackageText(output));
    }

    [Fact]
    public async Task AuthenticationTimeoutIsNotReportedAsCancellation()
    {
        using var directory =
            new CaptureTestDirectory(
                "auth-timeout");
        var output = Path.Combine(
            directory.Path,
            "capture");
        var runner = CreateRunner(
            CreateEnvironment(directory.Path),
            CreateOptions(),
            new QueueCatalogSource(),
            new DictionaryPageSource(
                new Dictionary<
                    CapturePageKey,
                    CapturePageAcquisition>()),
            new TestStorageProbe(),
            new ThrowingAuthenticator(
                new OperationCanceledException(
                    "authentication timeout")));

        var exception =
            await Assert.ThrowsAsync<
                CaptureOnlyException>(() =>
                runner.ExecuteAsync(
                    new CaptureOnlyCommand(
                        output,
                        "capture-auth-timeout"),
                    CancellationToken.None));

        Assert.Equal(
            CaptureOnlyExitCode.AuthenticationFailed,
            exception.ExitCode);
        AssertInterrupted(
            output,
            "capture-authentication-failed");
    }

    [Fact]
    public async Task InexactCatalogLeavesInterruptedPackage()
    {
        using var directory =
            new CaptureTestDirectory(
                "catalog-failure");
        var output = Path.Combine(
            directory.Path,
            "capture");
        var catalogSource =
            new QueueCatalogSource(
                new CaptureCatalogAcquisition(
                    ProviderRequestSucceeded: true,
                    IsExact: false,
                    SafetyMergeApplied: true,
                    IsReconstructed: false,
                    ParseFailureCount: 1,
                    ProviderContentSha256: null,
                    Catalog: null));
        var runner = CreateRunner(
            CreateEnvironment(directory.Path),
            CreateOptions(),
            catalogSource,
            new DictionaryPageSource(
                new Dictionary<
                    CapturePageKey,
                    CapturePageAcquisition>()),
            new TestStorageProbe());

        var exception =
            await Assert.ThrowsAsync<
                CaptureOnlyException>(() =>
                runner.ExecuteAsync(
                    new CaptureOnlyCommand(
                        output,
                        "capture-catalog-failure"),
                    CancellationToken.None));

        Assert.Equal(
            CaptureOnlyExitCode.CatalogRejected,
            exception.ExitCode);
        AssertInterrupted(
            output,
            "capture-catalog-rejected");
    }

    [Fact]
    public async Task CatalogSourceExceptionsMapAtInitialAndFinalFetch()
    {
        using var directory =
            new CaptureTestDirectory(
                "catalog-source-exceptions");
        var catalog = CreateCatalog(
            includeBand: false);
        var firstOutput = Path.Combine(
            directory.Path,
            "initial");
        var initialRunner = CreateRunner(
            CreateEnvironment(directory.Path),
            CreateOptions(enableBand: false),
            new QueueCatalogSource(),
            new DictionaryPageSource(
                new Dictionary<
                    CapturePageKey,
                    CapturePageAcquisition>()),
            new TestStorageProbe());

        var initial =
            await Assert.ThrowsAsync<
                CaptureOnlyException>(() =>
                initialRunner.ExecuteAsync(
                    new CaptureOnlyCommand(
                        firstOutput,
                        "capture-initial-catalog-error"),
                    CancellationToken.None));
        Assert.Equal(
            CaptureOnlyExitCode.CatalogRejected,
            initial.ExitCode);
        AssertInterrupted(
            firstOutput,
            "capture-catalog-rejected");

        var finalOutput = Path.Combine(
            directory.Path,
            "final");
        var finalRunner = CreateRunner(
            CreateEnvironment(directory.Path),
            CreateOptions(enableBand: false),
            new QueueCatalogSource(
                ExactCatalog(catalog)),
            new DictionaryPageSource(
                new Dictionary<
                    CapturePageKey,
                    CapturePageAcquisition>
                {
                    [new(
                        CaptureScopeKind.Solo,
                        "song-a",
                        "Solo_Guitar",
                        0)] = Page(
                            0,
                            1,
                            1,
                            1,
                            SoloEntry(
                                "player-a",
                                1),
                            pageSize: 1),
                }),
            new TestStorageProbe());

        var final =
            await Assert.ThrowsAsync<
                CaptureOnlyException>(() =>
                finalRunner.ExecuteAsync(
                    new CaptureOnlyCommand(
                        finalOutput,
                        "capture-final-catalog-error"),
                    CancellationToken.None));
        Assert.Equal(
            CaptureOnlyExitCode.CatalogRejected,
            final.ExitCode);
        AssertInterrupted(
            finalOutput,
            "capture-catalog-rejected");
    }

    [Fact]
    public async Task InvalidCatalogMaximumMetadataIsRejected()
    {
        using var directory =
            new CaptureTestDirectory(
                "invalid-catalog-maxima");
        var catalog = CreateCatalog(
            includeBand: false);
        var validScores =
            new Dictionary<string, SongMaxScores>(
                StringComparer.Ordinal)
            {
                ["song-a"] =
                    new SongMaxScores
                    {
                        MaxLeadScore = 100,
                    },
            };
        var baseline = ExactCatalog(
            catalog,
            maximumScores:
                validScores);
        var invalidAcquisitions =
            new CaptureCatalogAcquisition[]
            {
                baseline with
                {
                    PaginationMaximumScores =
                        new Dictionary<
                            string,
                            SongMaxScores>(
                            StringComparer.Ordinal)
                        {
                            ["unknown-song"] =
                                new SongMaxScores
                                {
                                    MaxLeadScore = 100,
                                },
                        },
                },
                baseline with
                {
                    PaginationMaximumScores =
                        new Dictionary<
                            string,
                            SongMaxScores>(
                            StringComparer.Ordinal)
                        {
                            ["song-a"] = null!,
                        },
                },
                baseline with
                {
                    PaginationMaximumScores =
                        new Dictionary<
                            string,
                            SongMaxScores>(
                            StringComparer.Ordinal)
                        {
                            ["song-a"] =
                                new SongMaxScores
                                {
                                    MaxLeadScore = 0,
                                },
                        },
                },
                baseline with
                {
                    PaginationMaximumScoresSha256 =
                        "invalid",
                },
                ExactCatalog(catalog) with
                {
                    PaginationMaximumScoresSha256 =
                        Hash("orphan-maxima"),
                },
                baseline with
                {
                    ProviderContentSha256 =
                        Hash("different-provider"),
                },
            };

        for (var index = 0;
             index < invalidAcquisitions.Length;
             index++)
        {
            var output = Path.Combine(
                directory.Path,
                $"capture-{index}");
            var runner = CreateRunner(
                CreateEnvironment(directory.Path),
                CreateOptions(enableBand: false),
                new QueueCatalogSource(
                    invalidAcquisitions[index]),
                new DictionaryPageSource(
                    new Dictionary<
                        CapturePageKey,
                        CapturePageAcquisition>()),
                new TestStorageProbe());

            var exception =
                await Assert.ThrowsAsync<
                    CaptureOnlyException>(() =>
                    runner.ExecuteAsync(
                        new CaptureOnlyCommand(
                            output,
                            $"invalid-maxima-{index}"),
                        CancellationToken.None));

            Assert.Equal(
                CaptureOnlyExitCode.CatalogRejected,
                exception.ExitCode);
            AssertInterrupted(
                output,
                "capture-catalog-rejected");
        }
    }

    [Fact]
    public async Task PageExceptionsAndUnauthorizedStatusMapToTypedFailures()
    {
        using var directory =
            new CaptureTestDirectory(
                "typed-page-failures");
        var catalog = CreateCatalog(
            includeBand: false);
        var cases =
            new (ICapturePageSource Source,
                 CaptureOnlyExitCode ExitCode,
                 string Marker)[]
            {
                (
                    new ThrowingPageSource(
                        new OperationCanceledException(
                            "internal timeout")),
                    CaptureOnlyExitCode.CaptureFailed,
                    "capture-incomplete"),
                (
                    new ThrowingPageSource(
                        new ScrapeAuthenticationException(
                            "expired")),
                    CaptureOnlyExitCode
                        .AuthenticationFailed,
                    "capture-authentication-failed"),
                (
                    new DictionaryPageSource(
                        new Dictionary<
                            CapturePageKey,
                            CapturePageAcquisition>
                        {
                            [new(
                                CaptureScopeKind.Solo,
                                "song-a",
                                "Solo_Guitar",
                                0)] =
                                new CapturePageAcquisition(
                                    CapturePageAcquisitionStatus
                                        .Unauthorized,
                                    0,
                                    100,
                                    0,
                                    0,
                                    [],
                                    1,
                                    0,
                                    IsExact: false),
                        }),
                    CaptureOnlyExitCode
                        .AuthenticationFailed,
                    "capture-authentication-failed"),
            };

        for (var index = 0;
             index < cases.Length;
             index++)
        {
            var output = Path.Combine(
                directory.Path,
                $"capture-{index}");
            var runner = CreateRunner(
                CreateEnvironment(directory.Path),
                CreateOptions(enableBand: false),
                new QueueCatalogSource(
                    ExactCatalog(catalog)),
                cases[index].Source,
                new TestStorageProbe());

            var exception =
                await Assert.ThrowsAsync<
                    CaptureOnlyException>(() =>
                    runner.ExecuteAsync(
                        new CaptureOnlyCommand(
                            output,
                            $"typed-failure-{index}"),
                        CancellationToken.None));

            Assert.Equal(
                cases[index].ExitCode,
                exception.ExitCode);
            AssertInterrupted(
                output,
                cases[index].Marker);
        }
    }

    [Fact]
    public async Task InvalidPageMetadataFailsClosed()
    {
        using var directory =
            new CaptureTestDirectory(
                "invalid-page-metadata");
        var catalog = CreateCatalog(
            includeBand: false);
        var baseline = Page(
            0,
            1,
            1,
            1,
            SoloEntry("player-a", 1),
            pageSize: 1);
        var invalidPages =
            new CapturePageAcquisition[]
            {
                baseline with { Entries = null! },
                baseline with { PageIndex = 1 },
                baseline with { PageSize = 0 },
                baseline with
                {
                    PageSize =
                        CapturePackageFormat
                            .MaximumPageSize + 1,
                },
                baseline with
                {
                    ProviderReportedTotalPages = -1,
                },
                baseline with
                {
                    ProviderReportedTotalPages =
                        CapturePackageFormat
                            .MaximumRequestRecords + 1,
                },
                baseline with
                {
                    ProviderReportedTotalEntries =
                        null,
                },
                baseline with
                {
                    ProviderReportedTotalEntries =
                        -1,
                },
                baseline with
                {
                    ProviderReportedTotalEntries =
                        CapturePackageFormat
                            .MaximumScopeEntries + 1,
                },
                baseline with
                {
                    WireRequestCount = 0,
                },
                baseline with
                {
                    ProviderResponseBytes = -1,
                },
                baseline with
                {
                    Entries =
                    [
                        SoloEntry("player-a", 1),
                        SoloEntry("player-b", 2),
                    ],
                },
                baseline with
                {
                    Entries =
                    [
                        JsonSerializer
                            .SerializeToElement(1),
                    ],
                },
                baseline with
                {
                    Status =
                        CapturePageAcquisitionStatus
                            .EventNotFound,
                    ProviderReportedTotalPages = 1,
                },
                baseline with
                {
                    ProviderReportedTotalPages = 0,
                    ProviderReportedTotalEntries = 1,
                },
            };

        for (var index = 0;
             index < invalidPages.Length;
             index++)
        {
            var output = Path.Combine(
                directory.Path,
                $"capture-{index}");
            var runner = CreateRunner(
                CreateEnvironment(directory.Path),
                CreateOptions(enableBand: false),
                new QueueCatalogSource(
                    ExactCatalog(catalog)),
                new DictionaryPageSource(
                    new Dictionary<
                        CapturePageKey,
                        CapturePageAcquisition>
                    {
                        [new(
                            CaptureScopeKind.Solo,
                            "song-a",
                            "Solo_Guitar",
                            0)] =
                                invalidPages[index],
                    }),
                new TestStorageProbe());

            var exception =
                await Assert.ThrowsAsync<
                    CaptureOnlyException>(() =>
                    runner.ExecuteAsync(
                        new CaptureOnlyCommand(
                            output,
                            $"invalid-page-{index}"),
                        CancellationToken.None));
            Assert.Equal(
                CaptureOnlyExitCode.CaptureFailed,
                exception.ExitCode);
        }
    }

    [Fact]
    public async Task IncompletePageScopeLeavesInterruptedPackage()
    {
        using var directory =
            new CaptureTestDirectory(
                "page-failure");
        var output = Path.Combine(
            directory.Path,
            "capture");
        var catalog = CreateCatalog(
            includeBand: false);
        var pageSource = new DictionaryPageSource(
            new Dictionary<
                CapturePageKey,
                CapturePageAcquisition>
            {
                [new(
                    CaptureScopeKind.Solo,
                    "song-a",
                    "Solo_Guitar",
                    0)] = Page(
                        0,
                        2,
                        2,
                        1,
                        Entry("player-a")),
                [new(
                    CaptureScopeKind.Solo,
                    "song-a",
                    "Solo_Guitar",
                    1)] = new CapturePageAcquisition(
                        CapturePageAcquisitionStatus
                            .RequestFailure,
                        1,
                        100,
                        0,
                        null,
                        [],
                        3,
                        0,
                        IsExact: false),
            });
        var runner = CreateRunner(
            CreateEnvironment(directory.Path),
            CreateOptions(enableBand: false),
            new QueueCatalogSource(
                ExactCatalog(catalog)),
            pageSource,
            new TestStorageProbe());

        var exception =
            await Assert.ThrowsAsync<
                CaptureOnlyException>(() =>
                runner.ExecuteAsync(
                    new CaptureOnlyCommand(
                        output,
                        "capture-page-failure"),
                    CancellationToken.None));

        Assert.Equal(
            CaptureOnlyExitCode.CaptureFailed,
            exception.ExitCode);
        AssertInterrupted(
            output,
            "capture-incomplete");
    }

    [Fact]
    public async Task TransportDeadlineMapsToCaptureFailure()
    {
        using var directory =
            new CaptureTestDirectory(
                "transport-deadline");
        var output = Path.Combine(
            directory.Path,
            "capture");
        var catalog = CreateCatalog(
            includeBand: false);
        var runner = CreateRunner(
            CreateEnvironment(directory.Path),
            CreateOptions(enableBand: false),
            new QueueCatalogSource(
                ExactCatalog(catalog)),
            new ThrowingPageSource(
                new HttpRequestException(
                    "Capture page transport deadline was exceeded.")),
            new TestStorageProbe());

        var exception =
            await Assert.ThrowsAsync<
                CaptureOnlyException>(() =>
                runner.ExecuteAsync(
                    new CaptureOnlyCommand(
                        output,
                        "capture-transport-deadline"),
                    CancellationToken.None));

        Assert.Equal(
            CaptureOnlyExitCode.CaptureFailed,
            exception.ExitCode);
        AssertInterrupted(
            output,
            "capture-incomplete");
    }

    [Fact]
    public async Task DuplicateSoloIdentityAcrossPagesRejectsScope()
    {
        using var directory =
            new CaptureTestDirectory(
                "duplicate-solo-identity");
        var output = Path.Combine(
            directory.Path,
            "capture");
        var catalog = CreateCatalog(
            includeBand: false);
        var runner = CreateRunner(
            CreateEnvironment(directory.Path),
            CreateOptions(enableBand: false),
            new QueueCatalogSource(
                ExactCatalog(catalog)),
            new DictionaryPageSource(
                new Dictionary<
                    CapturePageKey,
                    CapturePageAcquisition>
                {
                    [new(
                        CaptureScopeKind.Solo,
                        "song-a",
                        "Solo_Guitar",
                        0)] = Page(
                            0,
                            2,
                            2,
                            1,
                            SoloEntry("player-a", 1),
                            pageSize: 1),
                    [new(
                        CaptureScopeKind.Solo,
                        "song-a",
                        "Solo_Guitar",
                        1)] = Page(
                            1,
                            2,
                            2,
                            1,
                            SoloEntry("player-a", 2),
                            pageSize: 1),
                }),
            new TestStorageProbe());

        var exception =
            await Assert.ThrowsAsync<
                CaptureOnlyException>(() =>
                runner.ExecuteAsync(
                    new CaptureOnlyCommand(
                        output,
                        "capture-duplicate"),
                    CancellationToken.None));

        Assert.Equal(
            CaptureOnlyExitCode.CaptureFailed,
            exception.ExitCode);
        AssertInterrupted(
            output,
            "capture-incomplete");
    }

    [Fact]
    public async Task SensitiveTransportMaterialIsNeverWritten()
    {
        using var directory =
            new CaptureTestDirectory(
                "sensitive-response");
        var output = Path.Combine(
            directory.Path,
            "capture");
        var catalog = CreateCatalog(
            includeBand: false);
        var sensitive = JsonDocument.Parse(
                """
                {
                  "rank": 1,
                  "authorization": "Bearer secret-access-token",
                  "endpointUrl": "https://private.invalid/path"
                }
                """)
            .RootElement
            .Clone();
        var runner = CreateRunner(
            CreateEnvironment(directory.Path),
            CreateOptions(enableBand: false),
            new QueueCatalogSource(
                ExactCatalog(catalog)),
            new DictionaryPageSource(
                new Dictionary<
                    CapturePageKey,
                    CapturePageAcquisition>
                {
                    [new(
                        CaptureScopeKind.Solo,
                        "song-a",
                        "Solo_Guitar",
                        0)] = Page(
                            0,
                            1,
                            1,
                            1,
                            sensitive),
                }),
            new TestStorageProbe());

        var exception =
            await Assert.ThrowsAsync<
                CaptureOnlyException>(() =>
                runner.ExecuteAsync(
                    new CaptureOnlyCommand(
                        output,
                        "capture-sensitive"),
                    CancellationToken.None));

        Assert.Equal(
            CaptureOnlyExitCode.CaptureFailed,
            exception.ExitCode);
        var packageText = ReadPackageText(output);
        Assert.DoesNotContain(
            "secret-access-token",
            packageText);
        Assert.DoesNotContain(
            "private.invalid",
            packageText);
    }

    [Fact]
    public async Task CatalogDriftRejectsSealing()
    {
        using var directory =
            new CaptureTestDirectory(
                "catalog-drift");
        var output = Path.Combine(
            directory.Path,
            "capture");
        var selected = CreateCatalog(
            includeBand: false);
        var changedHash =
            Hash("provider-changed");
        var changed = CreateCatalog(
            includeBand: false,
            providerHash: changedHash);
        var runner = CreateRunner(
            CreateEnvironment(directory.Path),
            CreateOptions(enableBand: false),
            new QueueCatalogSource(
                ExactCatalog(selected),
                ExactCatalog(
                    changed,
                    changedHash)),
            new DictionaryPageSource(
                new Dictionary<
                    CapturePageKey,
                    CapturePageAcquisition>
                {
                    [new(
                        CaptureScopeKind.Solo,
                        "song-a",
                        "Solo_Guitar",
                        0)] = Page(
                            0,
                            1,
                            1,
                            1,
                            Entry("player-a")),
                }),
            new TestStorageProbe());

        var exception =
            await Assert.ThrowsAsync<
                CaptureOnlyException>(() =>
                runner.ExecuteAsync(
                    new CaptureOnlyCommand(
                        output,
                        "capture-drift"),
                    CancellationToken.None));

        Assert.Equal(
            CaptureOnlyExitCode.CatalogRejected,
            exception.ExitCode);
        AssertInterrupted(
            output,
            "capture-catalog-rejected");
    }

    [Fact]
    public async Task FinalDiskReserveFailureRejectsSealing()
    {
        using var directory =
            new CaptureTestDirectory(
                "final-reserve");
        var output = Path.Combine(
            directory.Path,
            "capture");
        var catalog = CreateCatalog(
            includeBand: false);
        var storage = new TestStorageProbe
        {
            AvailableFreeSpaceSequence =
                new Queue<long>(
                [
                    512L * 1024 * 1024,
                    512L * 1024 * 1024,
                    1,
                ]),
        };
        var runner = CreateRunner(
            CreateEnvironment(
                directory.Path,
                reserveBytes: 1),
            CreateOptions(enableBand: false),
            new QueueCatalogSource(
                ExactCatalog(catalog),
                ExactCatalog(catalog)),
            new DictionaryPageSource(
                new Dictionary<
                    CapturePageKey,
                    CapturePageAcquisition>
                {
                    [new(
                        CaptureScopeKind.Solo,
                        "song-a",
                        "Solo_Guitar",
                        0)] = Page(
                            0,
                            1,
                            1,
                            1,
                            Entry("player-a")),
                }),
            storage);

        var exception =
            await Assert.ThrowsAsync<
                CaptureOnlyException>(() =>
                runner.ExecuteAsync(
                    new CaptureOnlyCommand(
                        output,
                        "capture-reserve"),
                    CancellationToken.None));

        Assert.Equal(
            CaptureOnlyExitCode.AdmissionRejected,
            exception.ExitCode);
        Assert.False(
            File.Exists(
                Path.Combine(
                    output,
                    TierZeroEvidenceFormat
                        .ManifestFileName)));
        AssertInterrupted(
            output,
            "capture-admission-rejected");
    }

    [Fact]
    public async Task PackageMaximumRejectsBeforeLeaderboardTraffic()
    {
        using var directory =
            new CaptureTestDirectory(
                "package-maximum");
        var output = Path.Combine(
            directory.Path,
            "capture");
        var catalog = CreateCatalog(
            includeBand: false);
        var pageSource =
            new CountingPageSource();
        var runner = CreateRunner(
            CreateEnvironment(
                directory.Path,
                maximumPackageBytes:
                    CaptureRootAdmission
                        .PreMetadataFinalPackageAllowanceBytes +
                    1),
            CreateOptions(enableBand: false),
            new QueueCatalogSource(
                ExactCatalog(catalog)),
            pageSource,
            new TestStorageProbe());

        var exception =
            await Assert.ThrowsAsync<
                CaptureOnlyException>(() =>
                runner.ExecuteAsync(
                    new CaptureOnlyCommand(
                        output,
                        "capture-maximum"),
                    CancellationToken.None));

        Assert.Equal(
            CaptureOnlyExitCode.AdmissionRejected,
            exception.ExitCode);
        Assert.Equal(0, pageSource.CallCount);
        AssertInterrupted(
            output,
            "capture-admission-rejected");
    }

    [Fact]
    public async Task CancellationLeavesInterruptedPackage()
    {
        using var directory =
            new CaptureTestDirectory(
                "cancellation");
        var output = Path.Combine(
            directory.Path,
            "capture");
        var catalog = CreateCatalog(
            includeBand: false);
        using var cancellation =
            new CancellationTokenSource();
        var runner = CreateRunner(
            CreateEnvironment(directory.Path),
            CreateOptions(enableBand: false),
            new QueueCatalogSource(
                ExactCatalog(catalog)),
            new CancellingPageSource(
                cancellation),
            new TestStorageProbe());

        await Assert.ThrowsAnyAsync<
            OperationCanceledException>(() =>
            runner.ExecuteAsync(
                new CaptureOnlyCommand(
                    output,
                    "capture-cancel"),
                cancellation.Token));

        AssertInterrupted(
            output,
            "capture-cancelled");
    }

    [Fact]
    public async Task TerminationSignalReturns130AndLeavesInterruptedPackage()
    {
        using var directory =
            new CaptureTestDirectory(
                "termination-cancellation");
        var outputPath = Path.Combine(
            directory.Path,
            "capture");
        var authenticator =
            new BlockingAuthenticator();
        using var cancellation =
            new CancellationTokenSource();
        using var output = new StringWriter();
        var execution =
            CaptureOnlyEntryPoint.RunAsync(
                new CaptureOnlyCommand(
                    outputPath,
                    "capture-terminated"),
                CreateEnvironment(directory.Path),
                CreateOptions(enableBand: false),
                new CaptureOnlyCompositionOverrides(
                    Authenticator: authenticator,
                    CatalogSource:
                        new QueueCatalogSource(),
                    PageSource:
                        new DictionaryPageSource(
                            new Dictionary<
                                CapturePageKey,
                                CapturePageAcquisition>()),
                    StorageProbe:
                        new TestStorageProbe()),
                output,
                cancellation.Token);
        await authenticator.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        CaptureProcessSignals.RequestTermination(
            cancellation);
        var exitCode = await execution;

        Assert.Equal(
            (int)CaptureOnlyExitCode.Cancelled,
            exitCode);
        Assert.Contains(
            "\"kind\":\"Cancelled\"",
            output.ToString());
        Assert.DoesNotContain(
            "signal",
            output.ToString(),
            StringComparison.OrdinalIgnoreCase);
        AssertInterrupted(
            outputPath,
            "capture-cancelled");
    }

    [Fact]
    public async Task CancellationIsRecheckedImmediatelyAfterAuthentication()
    {
        using var directory =
            new CaptureTestDirectory(
                "post-auth-cancellation");
        var outputPath = Path.Combine(
            directory.Path,
            "capture");
        using var cancellation =
            new CancellationTokenSource();
        var runner = CreateRunner(
            CreateEnvironment(directory.Path),
            CreateOptions(enableBand: false),
            new QueueCatalogSource(),
            new DictionaryPageSource(
                new Dictionary<
                    CapturePageKey,
                    CapturePageAcquisition>()),
            new TestStorageProbe(),
            new CancellingAfterAuthAuthenticator(
                cancellation));

        await Assert.ThrowsAsync<
            OperationCanceledException>(() =>
            runner.ExecuteAsync(
                new CaptureOnlyCommand(
                    outputPath,
                    "capture-post-auth-cancel"),
                cancellation.Token));

        AssertInterrupted(
            outputPath,
            "capture-cancelled");
    }

    [Fact]
    public async Task SealFailureReturnsTypedCodeAndLeavesUnsealedPackage()
    {
        using var directory =
            new CaptureTestDirectory(
                "seal-failure");
        var output = Path.Combine(
            directory.Path,
            "capture");
        var catalog = CreateCatalog(
            includeBand: false);
        var clock = new SequenceTimeProvider(
            new DateTimeOffset(
                2026,
                9,
                12,
                12,
                0,
                0,
                TimeSpan.Zero),
            new DateTimeOffset(
                2026,
                9,
                12,
                12,
                2,
                0,
                TimeSpan.Zero),
            new DateTimeOffset(
                2026,
                9,
                12,
                12,
                1,
                0,
                TimeSpan.Zero),
            new DateTimeOffset(
                2026,
                9,
                12,
                12,
                3,
                0,
                TimeSpan.Zero));
        var runner = CreateRunner(
            CreateEnvironment(directory.Path),
            CreateOptions(enableBand: false),
            new QueueCatalogSource(
                ExactCatalog(catalog),
                ExactCatalog(catalog)),
            new DictionaryPageSource(
                new Dictionary<
                    CapturePageKey,
                    CapturePageAcquisition>
                {
                    [new(
                        CaptureScopeKind.Solo,
                        "song-a",
                        "Solo_Guitar",
                        0)] = Page(
                            0,
                            1,
                            1,
                            1,
                            Entry("player-a")),
                }),
            new TestStorageProbe(),
            timeProvider: clock);

        var exception =
            await Assert.ThrowsAsync<
                CaptureOnlyException>(() =>
                runner.ExecuteAsync(
                    new CaptureOnlyCommand(
                        output,
                        "capture-seal"),
                    CancellationToken.None));

        Assert.Equal(
            CaptureOnlyExitCode.SealFailed,
            exception.ExitCode);
        Assert.False(
            File.Exists(
                Path.Combine(
                    output,
                    TierZeroEvidenceFormat
                        .ManifestFileName)));
        AssertInterrupted(
            output,
            "capture-seal-failed");
    }

    private static CaptureOnlyRunner CreateRunner(
        CaptureOnlyExecutionEnvironment environment,
        ScraperOptions options,
        ICaptureCatalogSource catalogSource,
        ICapturePageSource pageSource,
        ICaptureStorageProbe storage,
        ICaptureAuthenticator? authenticator = null,
        TimeProvider? timeProvider = null) =>
        new(
            environment,
            options,
            authenticator ??
                new StaticAuthenticator(),
            catalogSource,
            pageSource,
            storage,
            timeProvider ??
                new FixedTimeProvider(
                    new DateTimeOffset(
                        2026,
                        9,
                        12,
                        12,
                        0,
                        0,
                        TimeSpan.Zero)));

    private static CaptureOnlyExecutionEnvironment
        CreateEnvironment(
            string root,
            long reserveBytes = 0,
            int responseShardBytes = 64 * 1024 * 1024,
            long maximumPackageBytes =
                256L * 1024 * 1024) =>
        new(
            new CaptureRootPolicyOptions(
                root,
                null,
                TestOnly: true),
            new CapturePackageStoragePolicy(
                maximumPackageBytes,
                reserveBytes,
                10),
            new TierZeroBuildIdentity(
                new string('a', 40),
                $"sha256:{new string('b', 64)}",
                new string('c', 40),
                "1.0.202"),
            responseShardBytes,
            "capture-only-tests");

    private static Dictionary<string, string?>
        ValidCaptureEnvironmentValues(
        string root) =>
        new(StringComparer.Ordinal)
        {
            [CaptureOnlyExecutionEnvironment
                .ApprovedRootEnvironment] =
                root,
            [CaptureOnlyExecutionEnvironment
                .ApprovedDeviceEnvironment] =
                TierZeroRegularFile
                    .GetFileSystemDeviceIdentity(
                        root),
            [CaptureOnlyExecutionEnvironment
                .MaximumPackageBytesEnvironment] =
                "268435456",
            [CaptureOnlyExecutionEnvironment
                .MinimumReserveBytesEnvironment] =
                "0",
            [CaptureOnlyExecutionEnvironment
                .MaximumRetainedPackagesEnvironment] =
                "2",
            [CaptureOnlyExecutionEnvironment
                .GitCommitEnvironment] =
                new string('a', 40),
            [CaptureOnlyExecutionEnvironment
                .ImageDigestEnvironment] =
                $"sha256:{new string('b', 64)}",
            [CaptureOnlyExecutionEnvironment
                .ImageRevisionEnvironment] =
                new string('c', 40),
            [CaptureOnlyExecutionEnvironment
                .ResponseShardBytesEnvironment] =
                "67108864",
            [CaptureOnlyExecutionEnvironment
                .PaginationMaximumScoresPathEnvironment] =
                null,
        };

    private static ScraperOptions CreateOptions(
        bool enableBand = true,
        bool enableSolo = true) =>
        new()
        {
            QueryLead = enableSolo,
            QueryBass = false,
            QueryVocals = false,
            QueryDrums = false,
            QueryProLead = false,
            QueryProBass = false,
            QueryProVocals = false,
            QueryProCymbals = false,
            QueryProDrums = false,
            EnableBandScraping = enableBand,
            DeviceAuthPath = "unused-device-auth.json",
        };

    private static CaptureCatalogArtifact CreateCatalog(
        bool includeBand = true,
        string? providerHash = null)
    {
        var songs = new[] { "song-a", "song-b" }
            .Select(songId =>
                new CaptureCatalogSong(
                    songId,
                    CapturePackageFormat
                        .SoloInstrumentOrder
                        .Select(instrument =>
                            new CaptureCatalogScopeSupport(
                                CaptureScopeKind.Solo,
                                instrument,
                                songId == "song-a" &&
                                instrument ==
                                    "Solo_Guitar"
                                    ? CaptureCatalogSupportStatus
                                        .Supported
                                    : CaptureCatalogSupportStatus
                                        .Unsupported))
                        .Concat(
                            CapturePackageFormat
                                .BandTypeOrder
                                .Select(bandType =>
                                    new CaptureCatalogScopeSupport(
                                        CaptureScopeKind.Band,
                                        bandType,
                                        includeBand &&
                                        songId == "song-a" &&
                                        bandType ==
                                            "Band_Duets"
                                            ? CaptureCatalogSupportStatus
                                                .Supported
                                            : CaptureCatalogSupportStatus
                                                .Unsupported)))
                        .ToArray()))
            .ToArray();
        return CapturePackageContract.ValidateCatalog(
            new CaptureCatalogArtifact(
                CapturePackageFormat.CatalogFormatId,
                CapturePackageFormat.CatalogSchemaVersion,
                CaptureCatalogBuilder
                    .CatalogVersionFromProviderHash(
                        providerHash ??
                        Hash("provider-catalog")),
                songs.Length,
                songs));
    }

    private static CaptureCatalogAcquisition ExactCatalog(
        CaptureCatalogArtifact catalog,
        string? providerHash = null,
        IReadOnlyDictionary<string, SongMaxScores>?
            maximumScores = null) =>
        new(
            ProviderRequestSucceeded: true,
            IsExact: true,
            SafetyMergeApplied: false,
            IsReconstructed: false,
            ParseFailureCount: 0,
            providerHash ?? Hash("provider-catalog"),
            catalog,
            maximumScores,
            maximumScores is null
                ? null
                : Hash("pagination-maximums"));

    private static CapturePageAcquisition Page(
        int pageIndex,
        int totalPages,
        long totalEntries,
        long requestCount,
        JsonElement entry,
        int pageSize = 100) =>
        new(
            CapturePageAcquisitionStatus.Success,
            pageIndex,
            pageSize,
            totalPages,
            totalEntries,
            [entry],
            requestCount,
            250,
            IsExact: true);

    private static JsonElement Entry(string id) =>
        SoloEntry(id, 1);

    private static JsonElement SoloEntry(
        string accountId,
        int rank,
        int score = 100) =>
        CaptureEntryContracts.Project(
            new LeaderboardEntry
            {
                AccountId = accountId,
                Rank = rank,
                Percentile = 1.0,
                Score = score,
                Accuracy = 1_000_000,
                IsFullCombo = true,
                Stars = 5,
                Season = 1,
                Difficulty = 3,
                EndTime = "2026-09-12T10:00:00Z",
            });

    private static JsonElement BandEntry(
        string firstAccountId,
        string secondAccountId,
        int rank,
        int score = 200)
    {
        var members = new[]
        {
            firstAccountId,
            secondAccountId,
        };
        return CaptureEntryContracts.Project(
            new BandLeaderboardEntry
            {
                TeamKey = string.Join(
                    ':',
                    members.OrderBy(
                        static value => value,
                        StringComparer.OrdinalIgnoreCase)),
                TeamMembers = members,
                Score = score,
                Accuracy = 1_000_000,
                IsFullCombo = true,
                Stars = 5,
                Difficulty = 3,
                Season = 1,
                Rank = rank,
                Percentile = 1.0,
                EndTime = "2026-09-12T10:00:00Z",
                InstrumentCombo = "0:1",
                MemberStats =
                [
                    new BandMemberStats
                    {
                        MemberIndex = 0,
                        AccountId = firstAccountId,
                        InstrumentId = 0,
                        Score = score / 2,
                        Accuracy = 1_000_000,
                        IsFullCombo = true,
                        Stars = 5,
                        Difficulty = 3,
                    },
                    new BandMemberStats
                    {
                        MemberIndex = 1,
                        AccountId = secondAccountId,
                        InstrumentId = 1,
                        Score = score / 2,
                        Accuracy = 1_000_000,
                        IsFullCombo = true,
                        Stars = 5,
                        Difficulty = 3,
                    },
                ],
            });
    }

    private static Song CreateSong(
        string songId,
        Action<In> configure)
    {
        var intensity = new In();
        configure(intensity);
        return new Song
        {
            track = new Track
            {
                su = songId,
                tt = songId,
                @in = intensity,
            },
        };
    }

    private static string Hash(string value) =>
        TierZeroCanonicalJson.Sha256Hex(
            System.Text.Encoding.UTF8
                .GetBytes(value));

    private static CaptureEntryIdentity
        ValidateCaptureEntry(
        CaptureResponseKind responseKind,
        string leaderboardType,
        object entry) =>
        ValidateCaptureEntry(
            responseKind,
            leaderboardType,
            entry,
            RequireSafeCaptureText,
            ThrowCaptureEntryFailure);

    private static CaptureEntryIdentity
        ValidateCaptureEntry(
        CaptureResponseKind responseKind,
        string leaderboardType,
        object entry,
        Action<string?, string> requireSafeText,
        Action<CapturePackageFailureKind, string>
            invalid) =>
        CaptureEntryContracts.ValidateAndIdentify(
            responseKind,
            leaderboardType,
            JsonSerializer.SerializeToElement(
                entry,
                TierZeroCanonicalJson
                    .SerializerOptions),
            requireSafeText,
            invalid);

    private static void RequireSafeCaptureText(
        string? value,
        string description)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CapturePackageException(
                CapturePackageFailureKind
                    .InvalidMetadata,
                $"{description} is unsafe.");
        }
    }

    private static void ThrowCaptureEntryFailure(
        CapturePackageFailureKind kind,
        string message) =>
        throw new CapturePackageException(
            kind,
            message);

    private static void AssertInterrupted(
        string packageRoot,
        string expectedError)
    {
        var statePath = Path.Combine(
            packageRoot,
            TierZeroEvidenceFormat.StateFileName);
        Assert.True(File.Exists(statePath));
        using var document = JsonDocument.Parse(
            File.ReadAllText(statePath));
        Assert.Equal(
            "interrupted",
            document.RootElement
                .GetProperty("status")
                .GetString());
        Assert.Equal(
            expectedError,
            document.RootElement
                .GetProperty("error")
                .GetString());
        Assert.False(
            File.Exists(
                Path.Combine(
                    packageRoot,
                    TierZeroEvidenceFormat
                        .ManifestFileName)));
    }

    private static string ReadPackageText(
        string packageRoot) =>
        string.Join(
            "\n",
            Directory
                .EnumerateFiles(
                    packageRoot,
                    "*",
                    SearchOption.AllDirectories)
                .Select(File.ReadAllText));

    private static void AssertCaptureFailure(
        CaptureOnlyExitCode expected,
        Action action)
    {
        var exception =
            Assert.Throws<CaptureOnlyException>(
                action);
        Assert.Equal(expected, exception.ExitCode);
    }

    private static CaptureOnlyFailureKind
        FailureKindFor(
            CaptureOnlyExitCode exitCode) =>
        exitCode switch
        {
            CaptureOnlyExitCode.Usage =>
                CaptureOnlyFailureKind.Usage,
            CaptureOnlyExitCode.RootRejected =>
                CaptureOnlyFailureKind.RootRejected,
            CaptureOnlyExitCode.AdmissionRejected =>
                CaptureOnlyFailureKind.AdmissionRejected,
            CaptureOnlyExitCode.AuthenticationFailed =>
                CaptureOnlyFailureKind.AuthenticationFailed,
            CaptureOnlyExitCode.CatalogRejected =>
                CaptureOnlyFailureKind.CatalogRejected,
            CaptureOnlyExitCode.CaptureFailed =>
                CaptureOnlyFailureKind.CaptureFailed,
            CaptureOnlyExitCode.SealFailed =>
                CaptureOnlyFailureKind.SealFailed,
            _ => throw new ArgumentOutOfRangeException(
                nameof(exitCode)),
        };

    private static async Task<ProgramResult>
        RunProgramAsync(
            string workingDirectory,
            params string[] arguments)
    {
        var startInfo =
            new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
        startInfo.ArgumentList.Add(
            typeof(Program).Assembly.Location);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process =
            System.Diagnostics.Process.Start(
                startInfo) ??
            throw new InvalidOperationException(
                "Could not start FSTService.");
        var stdout =
            process.StandardOutput.ReadToEndAsync();
        var stderr =
            process.StandardError.ReadToEndAsync();
        using var timeout =
            new CancellationTokenSource(
                TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(
            timeout.Token);
        await Task.WhenAll(stdout, stderr);
        return new ProgramResult(
            process.ExitCode,
            string.Concat(
                stdout.Result,
                "\n",
                stderr.Result));
    }

    private sealed record ProgramResult(
        int ExitCode,
        string Output);

    private readonly record struct CapturePageKey(
        CaptureScopeKind Kind,
        string SongId,
        string LeaderboardType,
        int PageIndex);

    private sealed class StaticAuthenticator
        : ICaptureAuthenticator
    {
        public Task<CaptureAuthentication>
            AuthenticateAsync(
                CancellationToken cancellationToken)
        {
            cancellationToken
                .ThrowIfCancellationRequested();
            return Task.FromResult(
                new CaptureAuthentication(
                    "secret-access-token",
                    "capture-caller-account"));
        }

    }

    private sealed class UnknownLengthContent(
        byte[] content)
        : HttpContent
    {
        protected override Task
            SerializeToStreamAsync(
                Stream stream,
                System.Net.TransportContext? context)
            => stream.WriteAsync(content)
                .AsTask();

        protected override bool TryComputeLength(
            out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class CountingAuthenticator
        : ICaptureAuthenticator
    {
        internal int CallCount { get; private set; }

        public Task<CaptureAuthentication>
            AuthenticateAsync(
                CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(
                new CaptureAuthentication(
                    "secret-access-token",
                    "capture-caller-account"));
        }
    }

    private sealed class ThrowingAuthenticator
        : ICaptureAuthenticator
    {
        private readonly Exception _exception;

        internal ThrowingAuthenticator(
            Exception exception)
        {
            _exception = exception;
        }

        public Task<CaptureAuthentication>
            AuthenticateAsync(
                CancellationToken cancellationToken) =>
            Task.FromException<
                CaptureAuthentication>(
                _exception);
    }

    private sealed class BlockingAuthenticator
        : ICaptureAuthenticator
    {
        internal TaskCompletionSource Started
        {
            get;
        } = new(
            TaskCreationOptions
                .RunContinuationsAsynchronously);

        public async Task<CaptureAuthentication>
            AuthenticateAsync(
                CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            throw new InvalidOperationException(
                "Unreachable.");
        }
    }

    private sealed class CancellingAfterAuthAuthenticator
        : ICaptureAuthenticator
    {
        private readonly CancellationTokenSource
            _cancellation;

        internal CancellingAfterAuthAuthenticator(
            CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public Task<CaptureAuthentication>
            AuthenticateAsync(
                CancellationToken cancellationToken)
        {
            _cancellation.Cancel();
            return Task.FromResult(
                new CaptureAuthentication(
                    "secret-access-token",
                    "capture-caller-account"));
        }
    }

    private sealed class QueueCatalogSource
        : ICaptureCatalogSource
    {
        private readonly Queue<
            CaptureCatalogAcquisition> _results;

        internal QueueCatalogSource(
            params CaptureCatalogAcquisition[]
                results)
        {
            _results = new Queue<
                CaptureCatalogAcquisition>(
                results);
        }

        public Task<CaptureCatalogAcquisition>
            FetchAsync(
                CancellationToken cancellationToken)
        {
            cancellationToken
                .ThrowIfCancellationRequested();
            if (_results.Count == 0)
            {
                return Task.FromException<
                    CaptureCatalogAcquisition>(
                    new InvalidOperationException(
                        "No catalog result."));
            }
            return Task.FromResult(
                _results.Dequeue());
        }
    }

    private sealed class DictionaryPageSource
        : ICapturePageSource
    {
        private readonly IReadOnlyDictionary<
            CapturePageKey,
            CapturePageAcquisition> _pages;

        internal DictionaryPageSource(
            IReadOnlyDictionary<
                CapturePageKey,
                CapturePageAcquisition> pages)
        {
            _pages = pages;
        }

        internal System.Collections.Concurrent
            .ConcurrentQueue<CapturePageKey>
            RequestedKeys
        { get; } = new();

        public Task<CapturePageAcquisition>
            FetchAsync(
                CaptureAuthentication authentication,
                CaptureScopeKind scopeKind,
                string songId,
                string leaderboardType,
                int pageIndex,
                CancellationToken cancellationToken)
        {
            cancellationToken
                .ThrowIfCancellationRequested();
            var key = new CapturePageKey(
                scopeKind,
                songId,
                leaderboardType,
                pageIndex);
            RequestedKeys.Enqueue(key);
            return _pages.TryGetValue(
                    key,
                    out var page)
                ? Task.FromResult(page)
                : Task.FromResult(
                    new CapturePageAcquisition(
                        CapturePageAcquisitionStatus
                            .RequestFailure,
                        pageIndex,
                        100,
                        0,
                        null,
                        [],
                        1,
                        0,
                        IsExact: false));
        }
    }

    private sealed class ThrowingPageSource(
        Exception exception)
        : ICapturePageSource
    {
        public Task<CapturePageAcquisition>
            FetchAsync(
                CaptureAuthentication authentication,
                CaptureScopeKind scopeKind,
                string songId,
                string leaderboardType,
                int pageIndex,
                CancellationToken cancellationToken) =>
            Task.FromException<
                CapturePageAcquisition>(
                exception);
    }

    private sealed class DelayedPageSource
        : ICapturePageSource,
          ICapturePageSourceMetrics
    {
        private readonly IReadOnlyDictionary<
            CapturePageKey,
            (CapturePageAcquisition Page,
             TimeSpan Delay)> _pages;
        private long _totalWireRequestCount;

        internal DelayedPageSource(
            IReadOnlyDictionary<
                CapturePageKey,
                (CapturePageAcquisition Page,
                 TimeSpan Delay)> pages)
        {
            _pages = pages;
        }

        internal System.Collections.Concurrent
            .ConcurrentQueue<int>
            CompletionOrder
        { get; } = new();

        public long TotalWireRequestCount =>
            Volatile.Read(
                ref _totalWireRequestCount);

        public Task QuiesceAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken
                .ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public async Task<CapturePageAcquisition>
            FetchAsync(
                CaptureAuthentication authentication,
                CaptureScopeKind scopeKind,
                string songId,
                string leaderboardType,
                int pageIndex,
                CancellationToken cancellationToken)
        {
            var key = new CapturePageKey(
                scopeKind,
                songId,
                leaderboardType,
                pageIndex);
            var item = _pages[key];
            Interlocked.Add(
                ref _totalWireRequestCount,
                item.Page.WireRequestCount);
            await Task.Delay(
                item.Delay,
                cancellationToken);
            if (pageIndex > 0)
                CompletionOrder.Enqueue(pageIndex);
            return item.Page;
        }
    }

    private sealed class QuiescingPageSource(
        CapturePageAcquisition page)
        : ICapturePageSource,
          ICapturePageSourceMetrics
    {
        private readonly
            TaskCompletionSource _releaseDetachedSend =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
        private Task? _detachedSend;
        private long _totalWireRequestCount;

        public long TotalWireRequestCount =>
            Volatile.Read(
                ref _totalWireRequestCount);

        public bool Quiesced { get; private set; }

        public Task<CapturePageAcquisition>
            FetchAsync(
                CaptureAuthentication authentication,
                CaptureScopeKind scopeKind,
                string songId,
                string leaderboardType,
                int pageIndex,
                CancellationToken cancellationToken)
        {
            cancellationToken
                .ThrowIfCancellationRequested();
            Interlocked.Add(
                ref _totalWireRequestCount,
                page.WireRequestCount);
            _detachedSend = Task.Run(
                async () =>
                {
                    await _releaseDetachedSend.Task;
                    Interlocked.Increment(
                        ref _totalWireRequestCount);
                },
                CancellationToken.None);
            return Task.FromResult(page);
        }

        public async Task QuiesceAsync(
            CancellationToken cancellationToken)
        {
            Quiesced = true;
            _releaseDetachedSend
                .TrySetResult();
            if (_detachedSend is not null)
            {
                await _detachedSend.WaitAsync(
                    cancellationToken);
            }
        }
    }

    private sealed class ConcurrentCapturePageHandler
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage>
            SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
        {
            var page = int.Parse(
                request.RequestUri!
                    .Query.Split(
                        '&',
                        StringSplitOptions
                            .RemoveEmptyEntries)
                    .Single(static part =>
                        part.StartsWith(
                            "?page=",
                            StringComparison.Ordinal))
                    ["?page=".Length..]);
            await Task.Delay(
                page == 0
                    ? TimeSpan.FromMilliseconds(50)
                    : TimeSpan.FromMilliseconds(5),
                cancellationToken);
            return new HttpResponseMessage(
                System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    {
                      "page": {{page}},
                      "totalPages": 2,
                      "totalEntries": 2,
                      "entries": [
                        {
                          "percentile": 1.0,
                          "sessionHistory": [{
                            "endTime": "2026-09-12T10:00:00Z",
                            "trackedStats": {
                              "ACCURACY": 1000000,
                              "DIFFICULTY": 3,
                              "FULL_COMBO": 1,
                              "SCORE": 100,
                              "SEASON": 1,
                              "STARS_EARNED": 5
                            }
                          }],
                          "teamId": "player-{{page}}",
                          "rank": {{page + 1}}
                        }
                      ]
                    }
                    """,
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed class CancellingPageSource
        : ICapturePageSource
    {
        private readonly
            CancellationTokenSource _cancellation;

        internal CancellingPageSource(
            CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public Task<CapturePageAcquisition>
            FetchAsync(
                CaptureAuthentication authentication,
                CaptureScopeKind scopeKind,
                string songId,
                string leaderboardType,
                int pageIndex,
                CancellationToken cancellationToken)
        {
            _cancellation.Cancel();
            return Task.FromCanceled<
                CapturePageAcquisition>(
                cancellationToken);
        }
    }

    private sealed class CountingPageSource
        : ICapturePageSource
    {
        internal int CallCount { get; private set; }

        public Task<CapturePageAcquisition>
            FetchAsync(
                CaptureAuthentication authentication,
                CaptureScopeKind scopeKind,
                string songId,
                string leaderboardType,
                int pageIndex,
                CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(
                Page(
                    pageIndex,
                    1,
                    1,
                    1,
                    Entry("player-a")));
        }
    }

    private sealed class TestStorageProbe
        : ICaptureStorageProbe
    {
        private int _retainedSealedPackages;

        internal long AvailableFreeSpaceBytes
        {
            get;
            init;
        } = 512L * 1024 * 1024;

        internal Queue<long>?
            AvailableFreeSpaceSequence
        {
            get;
            init;
        }

        internal int RetainedSealedPackages
        {
            get => Volatile.Read(
                ref _retainedSealedPackages);
            set => Volatile.Write(
                ref _retainedSealedPackages,
                value);
        }

        public long GetAvailableFreeSpace(
            string approvedRoot) =>
            AvailableFreeSpaceSequence is
            { Count: > 0 }
                ? AvailableFreeSpaceSequence.Dequeue()
                : AvailableFreeSpaceBytes;

        public int CountRetainedSealedPackages(
            string approvedRoot,
            int stopAfter,
            CancellationToken cancellationToken)
        {
            cancellationToken
                .ThrowIfCancellationRequested();
            return RetainedSealedPackages;
        }

        public long GetPackageBytes(
            string packageRoot,
            CancellationToken cancellationToken)
        {
            cancellationToken
                .ThrowIfCancellationRequested();
            return Directory
                .EnumerateFiles(
                    packageRoot,
                    "*",
                    SearchOption.AllDirectories)
                .Sum(static path =>
                    new FileInfo(path).Length);
        }
    }

    private sealed class FaultingStorageProbe
        : ICaptureStorageProbe
    {
        public long GetAvailableFreeSpace(
            string approvedRoot) =>
            throw new IOException(
                "injected free-space failure");

        public int CountRetainedSealedPackages(
            string approvedRoot,
            int stopAfter,
            CancellationToken cancellationToken) =>
            0;

        public long GetPackageBytes(
            string packageRoot,
            CancellationToken cancellationToken) =>
            throw new IOException(
                "injected package-size failure");
    }

    private sealed class StubRunner
        : ICaptureOnlyRunner
    {
        private readonly Func<
            CaptureOnlyCommand,
            CancellationToken,
            Task<CaptureOnlyResult>> _run;

        internal StubRunner(
            Func<
                CaptureOnlyCommand,
                CancellationToken,
                Task<CaptureOnlyResult>> run)
        {
            _run = run;
        }

        public Task<CaptureOnlyResult> ExecuteAsync(
            CaptureOnlyCommand command,
            CancellationToken cancellationToken) =>
            _run(command, cancellationToken);
    }

    private sealed class FixedTimeProvider
        : TimeProvider
    {
        private readonly DateTimeOffset _value;

        internal FixedTimeProvider(
            DateTimeOffset value)
        {
            _value = value;
        }

        public override DateTimeOffset GetUtcNow() =>
            _value;
    }

    private sealed class SequenceTimeProvider
        : TimeProvider
    {
        private readonly Queue<DateTimeOffset>
            _values;
        private DateTimeOffset _last;

        internal SequenceTimeProvider(
            params DateTimeOffset[] values)
        {
            _values = new Queue<DateTimeOffset>(
                values);
            _last = values[^1];
        }

        public override DateTimeOffset GetUtcNow()
        {
            if (_values.Count > 0)
                _last = _values.Dequeue();
            return _last;
        }
    }

    private sealed class CaptureTestDirectory
        : IDisposable
    {
        internal CaptureTestDirectory(
            string name)
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
            {
                Directory.Delete(
                    Path,
                    recursive: true);
            }
        }
    }

    private sealed class EnvironmentVariableScope
        : IDisposable
    {
        private readonly Dictionary<string, string?>
            _prior = [];

        internal EnvironmentVariableScope(
            IReadOnlyDictionary<string, string?>
                values)
        {
            foreach (var (key, value) in values)
            {
                _prior[key] =
                    Environment.GetEnvironmentVariable(
                        key);
                Environment.SetEnvironmentVariable(
                    key,
                    value);
            }
        }

        public void Dispose()
        {
            foreach (var (key, value) in _prior)
            {
                Environment.SetEnvironmentVariable(
                    key,
                    value);
            }
        }
    }
}
