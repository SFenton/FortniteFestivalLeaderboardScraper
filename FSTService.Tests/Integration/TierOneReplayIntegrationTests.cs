using System.Text;
using FSTService.Scraping;
using FSTService.Scraping.Replay;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FSTService.Tests.Integration;

public sealed class TierOneReplayIntegrationTests
{
    [Fact]
    public async Task SameInputProducesExactProjectionParityInFreshDatabases()
    {
        using var directory = new ReplayIntegrationDirectory(
            "same-input-parity");
        var fixture = await TierOneReplayFixture.CreateAsync(
            directory.Path);
        var baselineOutput = Path.Combine(
            directory.Path,
            "baseline-output");
        var candidateOutput = Path.Combine(
            directory.Path,
            "candidate-output");

        var baseline = await ExecuteAsync(
            fixture,
            baselineOutput,
            attempt: 1);
        var candidate = await ExecuteAsync(
            fixture,
            candidateOutput,
            attempt: 2);

        var policy = new ReplayRootAdmission(
            new ReplayRootPolicyOptions(
                directory.Path,
                TestOnly: true,
                RollbackReserveBytes: 0));
        var comparisonPath = Path.Combine(
            directory.Path,
            "comparison.json");
        var admitted = policy.AdmitComparison(
            baselineOutput,
            candidateOutput,
            comparisonPath);
        var comparison = await new ReplayComparisonService()
            .CompareAsync(
                admitted.Baseline,
                admitted.Candidate,
                admitted.Report,
                new ReplayComparisonExpectations(
                    TierOneReplayFixture.Build.OciImageDigest,
                    TierOneReplayFixture.Build.GitCommit,
                    TierOneReplayFixture.Build.OciImageRevision,
                    1,
                    TierOneReplayFixture.Build.OciImageDigest,
                    TierOneReplayFixture.Build.GitCommit,
                    TierOneReplayFixture.Build.OciImageRevision,
                    2),
                CancellationToken.None);

        Assert.True(comparison.ExactParity);
        Assert.All(
            comparison.Datasets,
            static dataset => Assert.True(dataset.ExactParity));
        Assert.Equal(
            fixture.InputManifest.PackageRootHash,
            comparison.TierOneInputRootHash);
        Assert.True(File.Exists(comparisonPath));
        Assert.True(baseline.Metrics.InsertedRows > 0);
        Assert.True(candidate.Metrics.InsertedRows > 0);

        var projection = await ReadProjectionAsync(
            baselineOutput);
        Assert.Equal(2, projection.Count);
        Assert.Equal(
            ["account-a:account-b", "account-c:account-d"],
            projection.Select(static row => row.TeamKey));
        Assert.Equal([1, 2], projection.Select(static row => row.Rank));
        Assert.Equal([1000, 900], projection.Select(static row => row.Score));

        var entryPointConnection =
            SharedPostgresContainer
                .CreateEmptyDatabaseConnectionString();
        await TierOneReplayFixture.BootstrapDatabaseAsync(
            entryPointConnection,
            fixture.ReplayId,
            fixture.InputManifest.PackageRootHash!);
        var entryPointOutput = Path.Combine(
            directory.Path,
            "entrypoint-output");
        var entryPointEnvironment =
            TierOneReplayFixture.Environment(
                directory.Path,
                entryPointConnection);
        var executionExit = await ReplayEntryPoint.RunAsync(
        [
            "--replay-parent-package", fixture.ParentPackage,
            "--replay-package", fixture.InputPackage,
            "--replay-phase",
            ReplayPhaseCatalog.BandMaintenancePhaseId,
            "--replay-subphase",
            ReplayPhaseCatalog.CurrentProjectionSubphaseId,
            "--replay-output", entryPointOutput,
            "--replay-id", fixture.ReplayId,
            "--replay-attempt", "3",
            "--no-publication",
        ],
            entryPointEnvironment);
        Assert.Equal(
            (int)ReplayExitCode.Success,
            executionExit);

        var entryPointComparison = Path.Combine(
            directory.Path,
            "entrypoint-comparison.json");
        var comparisonExit = await ReplayEntryPoint.RunAsync(
        [
            "--replay-compare-baseline", baselineOutput,
            "--replay-compare-candidate", candidateOutput,
            "--replay-comparison-output", entryPointComparison,
            "--replay-baseline-image-digest",
            TierOneReplayFixture.Build.OciImageDigest,
            "--replay-candidate-image-digest",
            TierOneReplayFixture.Build.OciImageDigest,
            "--replay-baseline-git-commit",
            TierOneReplayFixture.Build.GitCommit,
            "--replay-candidate-git-commit",
            TierOneReplayFixture.Build.GitCommit,
            "--replay-baseline-revision",
            TierOneReplayFixture.Build.OciImageRevision,
            "--replay-candidate-revision",
            TierOneReplayFixture.Build.OciImageRevision,
            "--replay-baseline-attempt", "1",
            "--replay-candidate-attempt", "2",
            "--no-publication",
        ],
            entryPointEnvironment);
        Assert.Equal(
            (int)ReplayExitCode.Success,
            comparisonExit);
        Assert.True(File.Exists(entryPointComparison));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledExit = await ReplayEntryPoint.RunAsync(
        [
            "--replay-parent-package", fixture.ParentPackage,
            "--replay-package", fixture.InputPackage,
            "--replay-phase",
            ReplayPhaseCatalog.BandMaintenancePhaseId,
            "--replay-subphase",
            ReplayPhaseCatalog.CurrentProjectionSubphaseId,
            "--replay-output",
            Path.Combine(directory.Path, "cancelled-entrypoint"),
            "--replay-id", fixture.ReplayId,
            "--replay-attempt", "4",
            "--no-publication",
        ],
            entryPointEnvironment,
            cancelled.Token);
        Assert.Equal(
            (int)ReplayExitCode.Cancelled,
            cancelledExit);
    }

    [Fact]
    public async Task MissingMarkerRefusesTargetBeforeImportOrOutput()
    {
        using var directory = new ReplayIntegrationDirectory(
            "missing-marker");
        var fixture = await TierOneReplayFixture.CreateAsync(
            directory.Path);
        var connectionString =
            SharedPostgresContainer
                .CreateEmptyDatabaseConnectionString();
        var output = Path.Combine(directory.Path, "output");
        using var loggerFactory = LoggerFactory.Create(
            static builder => builder.SetMinimumLevel(LogLevel.None));
        var environment =
            TierOneReplayFixture.Environment(
                directory.Path,
                connectionString);
        var runner = new TierOneReplayRunner(
            new ReplayRootAdmission(environment.RootPolicy),
            new ReplayDatabaseTargetGuard(
                connectionString,
                null,
                allowTestServerAddress: true),
            environment,
            loggerFactory,
            new FixedTimeProvider());

        var exception = await Assert.ThrowsAsync<ReplayException>(
            () => runner.ExecuteAsync(
                TierOneReplayFixture.Command(
                    fixture,
                    output),
                CancellationToken.None));

        Assert.Equal(
            ReplayFailureKind.TargetRejected,
            exception.Kind);
        Assert.False(Directory.Exists(output));
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT to_regclass('public.band_entries') IS NULL";
        Assert.True(await command.ExecuteScalarAsync() is true);

        var entryPointConnection =
            SharedPostgresContainer
                .CreateEmptyDatabaseConnectionString();
        var entryPointOutput = Path.Combine(
            directory.Path,
            "entrypoint-output");
        var entryPointEnvironment =
            TierOneReplayFixture.Environment(
                directory.Path,
                entryPointConnection);
        var exitCode = await ReplayEntryPoint.RunAsync(
        [
            "--replay-parent-package",
            fixture.ParentPackage,
            "--replay-package",
            fixture.InputPackage,
            "--replay-phase",
            ReplayPhaseCatalog.BandMaintenancePhaseId,
            "--replay-subphase",
            ReplayPhaseCatalog.CurrentProjectionSubphaseId,
            "--replay-output",
            entryPointOutput,
            "--replay-id",
            fixture.ReplayId,
            "--replay-attempt",
            "2",
            "--no-publication",
        ],
            entryPointEnvironment);
        Assert.Equal(
            (int)ReplayExitCode.TargetRejected,
            exitCode);
        Assert.False(Directory.Exists(entryPointOutput));
    }

    [Fact]
    public async Task MarkerMismatchAndProductionControlTablesAreRejected()
    {
        using var directory = new ReplayIntegrationDirectory(
            "target-identity-rejection");
        var fixture = await TierOneReplayFixture.CreateAsync(
            directory.Path);
        var mismatchConnection =
            SharedPostgresContainer
                .CreateEmptyDatabaseConnectionString();
        await TierOneReplayFixture.BootstrapDatabaseAsync(
            mismatchConnection,
            "different-replay",
            fixture.InputManifest.PackageRootHash!);
        await using var mismatchSource =
            new ReplayDatabaseTargetGuard(
                    mismatchConnection,
                    null,
                    allowTestServerAddress: true)
                .CreateDataSource(
                    TierOneReplayBounds.Conservative);
        var mismatch = await Assert.ThrowsAsync<ReplayException>(
            () => new ReplayDatabaseTargetGuard(
                    mismatchConnection,
                    null,
                    allowTestServerAddress: true)
                .ValidateAsync(
                    mismatchSource,
                    fixture.ReplayId,
                    fixture.InputManifest.PackageRootHash!,
                    fixture.PhaseInputManifest.SourceDatabaseSystemIdentifier,
                    ReplayDatabaseTargetGuard.CreatedStatus,
                    TierOneReplayDatabaseSchema.Fingerprint,
                    CancellationToken.None));
        Assert.Equal(
            ReplayFailureKind.TargetRejected,
            mismatch.Kind);

        var sourceClusterConnection =
            SharedPostgresContainer
                .CreateEmptyDatabaseConnectionString();
        await TierOneReplayFixture.BootstrapDatabaseAsync(
            sourceClusterConnection,
            fixture.ReplayId,
            fixture.InputManifest.PackageRootHash!);
        string sourceSystemIdentifier;
        await using (var connection =
            new NpgsqlConnection(sourceClusterConnection))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT system_identifier::TEXT FROM pg_control_system()";
            sourceSystemIdentifier =
                (string)(await command.ExecuteScalarAsync())!;
        }
        var sourceClusterGuard =
            new ReplayDatabaseTargetGuard(
                sourceClusterConnection,
                null,
                allowTestServerAddress: true);
        await using var sourceClusterDataSource =
            sourceClusterGuard.CreateDataSource(
                TierOneReplayBounds.Conservative);
        var sourceCluster =
            await Assert.ThrowsAsync<ReplayException>(
                () => sourceClusterGuard.ValidateAsync(
                    sourceClusterDataSource,
                    fixture.ReplayId,
                    fixture.InputManifest.PackageRootHash!,
                    sourceSystemIdentifier,
                    ReplayDatabaseTargetGuard.CreatedStatus,
                    TierOneReplayDatabaseSchema.Fingerprint,
                    CancellationToken.None));
        Assert.Equal(
            ReplayFailureKind.TargetRejected,
            sourceCluster.Kind);

        var productionShapedConnection =
            SharedPostgresContainer
                .CreateEmptyDatabaseConnectionString();
        await TierOneReplayFixture.BootstrapDatabaseAsync(
            productionShapedConnection,
            fixture.ReplayId,
            fixture.InputManifest.PackageRootHash!);
        await using (var connection =
            new NpgsqlConnection(productionShapedConnection))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE scrape_publication_state (id BOOLEAN PRIMARY KEY)";
            await command.ExecuteNonQueryAsync();
        }
        var productionGuard = new ReplayDatabaseTargetGuard(
            productionShapedConnection,
            null,
            allowTestServerAddress: true);
        await using var productionSource =
            productionGuard.CreateDataSource(
                TierOneReplayBounds.Conservative);
        var productionShaped =
            await Assert.ThrowsAsync<ReplayException>(
                () => productionGuard.ValidateAsync(
                    productionSource,
                    fixture.ReplayId,
                    fixture.InputManifest.PackageRootHash!,
                    fixture.PhaseInputManifest.SourceDatabaseSystemIdentifier,
                    ReplayDatabaseTargetGuard.CreatedStatus,
                    TierOneReplayDatabaseSchema.Fingerprint,
                    CancellationToken.None));
        Assert.Equal(
            ReplayFailureKind.TargetRejected,
            productionShaped.Kind);

        var triggerConnection =
            SharedPostgresContainer
                .CreateEmptyDatabaseConnectionString();
        await TierOneReplayFixture.BootstrapDatabaseAsync(
            triggerConnection,
            fixture.ReplayId,
            fixture.InputManifest.PackageRootHash!);
        await using (var connection =
            new NpgsqlConnection(triggerConnection))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE FUNCTION fst_replay_control.reject_marker_change()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RETURN NEW;
                END
                $$;
                CREATE TRIGGER replay_marker_trigger
                BEFORE UPDATE ON fst_replay_control.target
                FOR EACH ROW
                EXECUTE FUNCTION
                    fst_replay_control.reject_marker_change();
                """;
            await command.ExecuteNonQueryAsync();
        }
        var triggerGuard = new ReplayDatabaseTargetGuard(
            triggerConnection,
            null,
            allowTestServerAddress: true);
        await using var triggerSource =
            triggerGuard.CreateDataSource(
                TierOneReplayBounds.Conservative);
        var input = await new TierOneReplayPackageReader().LoadAsync(
            new AdmittedReplayPaths(
                directory.Path,
                fixture.ParentPackage,
                fixture.InputPackage,
                Path.Combine(directory.Path, "unused-output")),
            ReplayPhaseCatalog.BandCurrentProjectionRefresh,
            CancellationToken.None);
        var trigger = await Assert.ThrowsAsync<ReplayException>(
            () => new TierOneReplayImporter().ImportAsync(
                triggerSource,
                input,
                CancellationToken.None));
        Assert.Equal(
            ReplayFailureKind.ImportRejected,
            trigger.Kind);
    }

    [Fact]
    public async Task CancelledPhaseLeavesUnsealedFailedAttempt()
    {
        using var directory = new ReplayIntegrationDirectory(
            "cancelled-attempt");
        var fixture = await TierOneReplayFixture.CreateAsync(
            directory.Path);
        var connectionString =
            SharedPostgresContainer
                .CreateEmptyDatabaseConnectionString();
        await TierOneReplayFixture.BootstrapDatabaseAsync(
            connectionString,
            fixture.ReplayId,
            fixture.InputManifest.PackageRootHash!);
        var output = Path.Combine(directory.Path, "output");
        var environment =
            TierOneReplayFixture.Environment(
                directory.Path,
                connectionString);
        using var loggerFactory = LoggerFactory.Create(
            static builder => builder.SetMinimumLevel(LogLevel.None));
        var runner = new TierOneReplayRunner(
            new ReplayRootAdmission(environment.RootPolicy),
            new ReplayDatabaseTargetGuard(
                connectionString,
                null,
                allowTestServerAddress: true),
            environment,
            loggerFactory,
            new FixedTimeProvider(),
            adapters: [new CancellingAdapter()]);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => runner.ExecuteAsync(
                TierOneReplayFixture.Command(fixture, output),
                CancellationToken.None));

        var verification = await TierZeroPackageVerifier.VerifyAsync(
            output);
        Assert.False(verification.IsValid);
        Assert.Contains(
            verification.Failures,
            static failure =>
                failure.Kind ==
                TierZeroVerificationFailureKind.UnsealedPackage);
        Assert.True(File.Exists(Path.Combine(
            output,
            TierOneReplayFormat.FailurePath.Replace(
                '/',
                Path.DirectorySeparatorChar))));
        Assert.Equal(
            ReplayDatabaseTargetGuard.FailedStatus,
            await ReadMarkerStatusAsync(connectionString));
    }

    [Fact]
    public async Task StaleReplayObjectsRejectFreshImportAndLeaveFailureAttempt()
    {
        using var directory = new ReplayIntegrationDirectory(
            "stale-replay-object");
        var fixture = await TierOneReplayFixture.CreateAsync(
            directory.Path);
        var connectionString =
            SharedPostgresContainer
                .CreateEmptyDatabaseConnectionString();
        await TierOneReplayFixture.BootstrapDatabaseAsync(
            connectionString,
            fixture.ReplayId,
            fixture.InputManifest.PackageRootHash!);
        await using (var connection =
            new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE band_current_projection_scope (id INTEGER)";
            await command.ExecuteNonQueryAsync();
        }
        var output = Path.Combine(directory.Path, "output");
        var environment =
            TierOneReplayFixture.Environment(
                directory.Path,
                connectionString);
        using var loggerFactory = LoggerFactory.Create(
            static builder => builder.SetMinimumLevel(LogLevel.None));
        var runner = new TierOneReplayRunner(
            new ReplayRootAdmission(environment.RootPolicy),
            new ReplayDatabaseTargetGuard(
                connectionString,
                null,
                allowTestServerAddress: true),
            environment,
            loggerFactory,
            new FixedTimeProvider());

        var exception = await Assert.ThrowsAsync<ReplayException>(
            () => runner.ExecuteAsync(
                TierOneReplayFixture.Command(fixture, output),
                CancellationToken.None));

        Assert.Equal(
            ReplayFailureKind.ImportRejected,
            exception.Kind);
        Assert.Equal(
            ReplayDatabaseTargetGuard.FailedStatus,
            await ReadMarkerStatusAsync(connectionString));
        var verification = await TierZeroPackageVerifier.VerifyAsync(
            output);
        Assert.Contains(
            verification.Failures,
            static failure =>
                failure.Kind ==
                TierZeroVerificationFailureKind.UnsealedPackage);
    }

    [Fact]
    public async Task CorruptionAndWrongParentAreRejectedBeforeDatabaseUse()
    {
        using var directory = new ReplayIntegrationDirectory(
            "package-rejection");
        var fixture = await TierOneReplayFixture.CreateAsync(
            directory.Path);
        var phase = ReplayPhaseCatalog.BandCurrentProjectionRefresh;
        var reader = new TierOneReplayPackageReader();
        var paths = new AdmittedReplayPaths(
            directory.Path,
            fixture.ParentPackage,
            fixture.InputPackage,
            Path.Combine(directory.Path, "unused-output"));
        var entryPath = Path.Combine(
            fixture.InputPackage,
            TierOneReplayFormat.EntriesPath.Replace(
                '/',
                Path.DirectorySeparatorChar));
        await File.AppendAllTextAsync(entryPath, "{}\n");

        var corruption = await Assert.ThrowsAsync<ReplayException>(
            () => reader.LoadAsync(
                paths,
                phase,
                CancellationToken.None));

        Assert.Equal(
            ReplayFailureKind.PackageRejected,
            corruption.Kind);

        using var otherDirectory = new ReplayIntegrationDirectory(
            "wrong-parent");
        var other = await TierOneReplayFixture.CreateAsync(
            otherDirectory.Path,
            replayId: "other-replay",
            parentVariant: "other");
        using var cleanDirectory = new ReplayIntegrationDirectory(
            "clean-input");
        var clean = await TierOneReplayFixture.CreateAsync(
            cleanDirectory.Path);
        var wrongParentPaths = new AdmittedReplayPaths(
            cleanDirectory.Path,
            other.ParentPackage,
            clean.InputPackage,
            Path.Combine(cleanDirectory.Path, "unused-output"));
        var mismatch = await Assert.ThrowsAsync<ReplayException>(
            () => reader.LoadAsync(
                wrongParentPaths,
                phase,
                CancellationToken.None));
        Assert.Equal(
            ReplayFailureKind.PackageRejected,
            mismatch.Kind);
    }

    [Fact]
    public async Task ComparisonRejectsIncompleteOutputEnvelope()
    {
        using var directory = new ReplayIntegrationDirectory(
            "incomplete-output");
        var fixture = await TierOneReplayFixture.CreateAsync(
            directory.Path);
        var incomplete = Path.Combine(
            directory.Path,
            "incomplete-output");
        await CreateIncompleteOutputAsync(
            fixture,
            incomplete);
        var report = Path.Combine(
            directory.Path,
            "comparison.json");

        var exception = await Assert.ThrowsAsync<ReplayException>(
            () => new ReplayComparisonService().CompareAsync(
                incomplete,
                incomplete,
                report,
                new ReplayComparisonExpectations(
                    TierOneReplayFixture.Build.OciImageDigest,
                    TierOneReplayFixture.Build.GitCommit,
                    TierOneReplayFixture.Build.OciImageRevision,
                    1,
                    TierOneReplayFixture.Build.OciImageDigest,
                    TierOneReplayFixture.Build.GitCommit,
                    TierOneReplayFixture.Build.OciImageRevision,
                    1),
                CancellationToken.None));

        Assert.Equal(
            ReplayFailureKind.ComparisonFailed,
            exception.Kind);
        Assert.False(File.Exists(report));
    }

    private static async Task<ReplayExecutionResult> ExecuteAsync(
        TierOneReplayFixture fixture,
        string output,
        int attempt)
    {
        var connectionString =
            SharedPostgresContainer
                .CreateEmptyDatabaseConnectionString();
        await TierOneReplayFixture.BootstrapDatabaseAsync(
            connectionString,
            fixture.ReplayId,
            fixture.InputManifest.PackageRootHash!);
        var environment =
            TierOneReplayFixture.Environment(
                fixture.Root,
                connectionString);
        using var loggerFactory = LoggerFactory.Create(
            static builder => builder.SetMinimumLevel(LogLevel.None));
        var runner = new TierOneReplayRunner(
            new ReplayRootAdmission(environment.RootPolicy),
            new ReplayDatabaseTargetGuard(
                connectionString,
                null,
                allowTestServerAddress: true),
            environment,
            loggerFactory,
            new FixedTimeProvider());
        var result = await runner.ExecuteAsync(
            TierOneReplayFixture.Command(
                fixture,
                output,
                attempt),
            CancellationToken.None);
        var verification = await TierZeroPackageVerifier.VerifyAsync(
            output);
        Assert.True(
            verification.IsValid,
            string.Join(
                Environment.NewLine,
                verification.Failures.Select(static failure =>
                    failure.Message)));
        Assert.Equal(
            ReplayDatabaseTargetGuard.CompletedStatus,
            await ReadMarkerStatusAsync(connectionString));
        await AssertNoPublicationTablesAsync(connectionString);
        return result;
    }

    private static async Task<IReadOnlyList<ReplayOutputProjectionRow>>
        ReadProjectionAsync(string output)
    {
        var verification = await TierZeroPackageVerifier.VerifyAsync(
            output);
        var envelope = Assert.IsType<TierZeroEvidenceManifest>(
            verification.Manifest);
        var artifact = TierOneReplayPackageReader.RequireArtifact(
            envelope,
            TierOneReplayFormat.ProjectionOutputPath);
        var bytes = await TierOneReplayPackageReader.ReadArtifactAsync(
            output,
            artifact,
            CancellationToken.None);
        return Encoding.UTF8.GetString(bytes)
            .Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
                TierZeroCanonicalJson
                    .Deserialize<ReplayOutputProjectionRow>(
                        Encoding.UTF8.GetBytes(line)))
            .ToArray();
    }

    private static async Task<string> ReadMarkerStatusAsync(
        string connectionString)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT status FROM fst_replay_control.target WHERE singleton = TRUE";
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task AssertNoPublicationTablesAsync(
        string connectionString)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT to_regclass('public.scrape_publication_state') IS NULL
               AND to_regclass('public.service_worker_status') IS NULL
               AND to_regclass('public.scrape_log') IS NULL
            """;
        Assert.True(await command.ExecuteScalarAsync() is true);
    }

    private static async Task CreateIncompleteOutputAsync(
        TierOneReplayFixture fixture,
        string output)
    {
        var metrics = new ReplayPhaseMetrics(
            1,
            1,
            1,
            1,
            1,
            1,
            1,
            0,
            1,
            0);
        var database = new ReplayDatabaseIdentity(
            "fst_replay_incomplete",
            "1",
            17,
            ["plpgsql@1.0"],
            TierOneReplayDatabaseSchema.Fingerprint);
        var outputManifest = new TierOnePhaseOutputManifest(
            TierOneReplayFormat.OutputFormatId,
            TierOneReplayFormat.Version,
            fixture.ReplayId,
            1,
            ReplayPhaseCatalog.BandMaintenancePhaseId,
            ReplayPhaseCatalog.CurrentProjectionSubphaseId,
            ReplayPhaseCatalog.CurrentProjectionAdapterVersion,
            fixture.ParentManifest.PackageRootHash!,
            fixture.InputManifest.PackageRootHash!,
            PhaseProgressCatalog.OperationId,
            PhaseProgressCatalog.PlanVersion,
            TierOneReplayFixture.Build,
            database,
            [],
            metrics,
            TierOneReplayFixture.CreatedAt,
            TierOneReplayFixture.CreatedAt.AddSeconds(1),
            true,
            null);
        var outputManifestBytes =
            TierOneReplayCanonical.SerializeOutput(outputManifest);
        var metricsBytes =
            TierZeroCanonicalJson.Serialize(metrics);
        var configuration =
            TierZeroConfigurationFingerprinter.Create(
                new Dictionary<string, string?>
                {
                    ["Replay:NoPublication"] = "true",
                },
                ["Replay:NoPublication"]);
        var writer = await TierZeroPackageWriter.CreateAsync(
            output,
            new TierZeroPackageDraft(
                "incomplete-output",
                fixture.ParentManifest.Source,
                TierOneReplayFixture.Build,
                new TierZeroDatabaseIdentity(
                    17,
                    ["plpgsql@1.0"],
                    TierOneReplayDatabaseSchema.Fingerprint),
                configuration,
                TierZeroSummaryReferences.Empty,
                [
                    new TierZeroParentRootHash(
                        "tier0-parent",
                        fixture.ParentManifest.PackageRootHash!),
                    new TierZeroParentRootHash(
                        "tier1-input",
                        fixture.InputManifest.PackageRootHash!),
                ],
                1,
                "tier1-replay-test",
                TierOneReplayFixture.CreatedAt));
        await writer.AddArtifactAsync(
            new TierZeroArtifactRegistration(
                "replay-resource-metrics",
                TierOneReplayFormat.MetricsPath,
                "application/json",
                1,
                1,
                metricsBytes.LongLength),
            metricsBytes);
        await writer.AddArtifactAsync(
            new TierZeroArtifactRegistration(
                "tier1-phase-output",
                TierOneReplayFormat.OutputManifestPath,
                "application/json",
                1,
                1,
                outputManifestBytes.LongLength),
            outputManifestBytes);
        await writer.SealAsync(
            TierOneReplayFixture.CreatedAt.AddSeconds(2));
    }

    private sealed class CancellingAdapter : IReplayPhaseAdapter
    {
        public ReplayPhaseDescriptor Descriptor =>
            ReplayPhaseCatalog.BandCurrentProjectionRefresh;

        public Task<ReplayPhaseExecutionResult> ExecuteAsync(
            NpgsqlDataSource dataSource,
            TierOneReplayInput input,
            CancellationToken cancellationToken) =>
            throw new OperationCanceledException();
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            TierOneReplayFixture.CreatedAt.AddHours(1);
    }

    private sealed class ReplayIntegrationDirectory : IDisposable
    {
        internal ReplayIntegrationDirectory(string name)
        {
            Path = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                ".test-temp",
                $"tier1-replay-{name}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
