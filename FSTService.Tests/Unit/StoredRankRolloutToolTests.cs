using FstStoredRankRollout;
using FSTService.Scraping;
using System.Text.Json;

namespace FSTService.Tests.Unit;

public sealed class StoredRankRolloutToolTests
{
    private const string TestServiceImage =
        "ghcr.io/sfenton/fstservice:test@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string TestServiceImageId =
        "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void Empty_source_mapping_does_not_require_a_projection()
    {
        var sourceClass = ManifestGenerator.Classify(
            publishedScrapeId: 1278,
            sourceKind: "empty",
            sourceScrapeId: 1276,
            projectionSourceSnapshotId: 0,
            projectionGeneration: null,
            projectionScopeSourceSnapshotId: null,
            projectionStatus: null);

        Assert.Equal(ScopeSourceClass.Empty, sourceClass);
    }

    [Fact]
    public void Coverage_requires_only_source_classes_present_in_publication()
    {
        var coverage = DeterministicRollout.BuildCoverage(
            [
                Scope("current", "Solo_Guitar", ScopeSourceClass.Current),
                Scope("empty", "Solo_Guitar", ScopeSourceClass.Empty),
            ],
            [],
            [],
            [],
            [ScopeSourceClass.Current, ScopeSourceClass.Empty]);

        Assert.DoesNotContain("Reused", coverage.MissingSourceClasses);
        Assert.DoesNotContain("SourceMismatch", coverage.MissingSourceClasses);
    }

    [Fact]
    public void Threshold_uses_exact_CSharp_truncation_instead_of_Postgres_rounding()
    {
        const int rawMaxScore = 99_999;
        const int leewayTenths = 1;

        var threshold = DeterministicRollout.CalculateThreshold(rawMaxScore, leewayTenths);
        var raw = rawMaxScore * (1.0 + leewayTenths / 1000.0);

        Assert.Equal(100_098, threshold);
        Assert.Equal((int)Math.Truncate(raw), threshold);
        Assert.Equal(100_099, (int)Math.Round(raw, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void Manifest_selection_and_fingerprint_are_seed_deterministic()
    {
        var candidates = GlobalLeaderboardScraper.AllInstruments
            .Select((instrument, index) => Scope(
                $"song-{index}",
                instrument,
                index == 0 ? ScopeSourceClass.Reused : ScopeSourceClass.Current,
                overlay: index == 1))
            .Append(Scope("empty", "Solo_Guitar", ScopeSourceClass.Empty))
            .Append(Scope("mismatch", "Solo_Bass", ScopeSourceClass.SourceMismatch))
            .ToArray();

        var firstSelection = DeterministicRollout.SelectScopes(
            candidates,
            GlobalLeaderboardScraper.AllInstruments,
            seed: 8472);
        var secondSelection = DeterministicRollout.SelectScopes(
            candidates.Reverse(),
            GlobalLeaderboardScraper.AllInstruments,
            seed: 8472);

        Assert.Equal(
            firstSelection.Select(static scope => scope.Id),
            secondSelection.Select(static scope => scope.Id));

        var first = Manifest(firstSelection, generatedAt: DateTimeOffset.Parse("2026-08-04T00:00:00Z"));
        var second = Manifest(secondSelection.Reverse().ToArray(), generatedAt: DateTimeOffset.Parse("2026-08-05T00:00:00Z"));
        Assert.Equal(
            DeterministicRollout.ComputeManifestFingerprint(first),
            DeterministicRollout.ComputeManifestFingerprint(second));
    }

    [Fact]
    public void Image_pin_rejects_mutable_or_unresolved_references()
    {
        Assert.True(RolloutImagePin.IsValid(TestServiceImage, TestServiceImageId));
        Assert.False(RolloutImagePin.IsValid(
            "ghcr.io/sfenton/fstservice:latest",
            TestServiceImageId));
        Assert.False(RolloutImagePin.IsValid(TestServiceImage, "sha256:short"));
        Assert.Throws<InvalidDataException>(() => RolloutImagePin.Validate(
            "ghcr.io/sfenton/fstservice:latest",
            TestServiceImageId));
    }

    [Fact]
    public void Database_attestation_rejects_same_named_clone_and_runtime_binding_drift()
    {
        var expected = new DatabaseIdentityEvidence
        {
            DatabaseName = "fstservice",
            SystemIdentifier = "123456789",
            ServerAddress = "172.20.0.2",
            ServerPort = 5432,
            UnixSocketDirectories = "/var/run/postgresql",
        };
        var manifest = new RolloutManifest
        {
            DatabaseIdentity = expected,
            ServiceDatabaseTarget = new ServiceDatabaseTarget
            {
                Host = "postgres",
                Port = 5432,
                Database = "fstservice",
                Username = "fst",
            },
            PostgresContainerId = "postgres-container",
            PostgresImageReference = "fst-postgres:17-repack",
            PostgresImageId =
                "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
            PostgresNetworkNames = ["fst-network"],
            PostgresNetworkAliases = ["postgres", "fst-postgres"],
            PostgresServerAddresses = ["172.20.0.2"],
            PostgresNetworkBindings = TestNetworkBindings("postgres-container"),
        };
        var clone = new DatabaseIdentityEvidence
        {
            DatabaseName = expected.DatabaseName,
            SystemIdentifier = "987654321",
            ServerAddress = "172.21.0.2",
            ServerPort = expected.ServerPort,
            UnixSocketDirectories = expected.UnixSocketDirectories,
        };

        var cloneReport = ReadOnlyPostgres.CompareDatabaseIdentity(
            manifest,
            clone);

        Assert.False(cloneReport.Passed);
        Assert.Contains("system-identifier", cloneReport.Failures);
        Assert.Contains("server-address", cloneReport.Failures);

        var wrongDatabase = new DatabaseIdentityEvidence
        {
            DatabaseName = "fstservice_clone",
            SystemIdentifier = expected.SystemIdentifier,
            ServerAddress = expected.ServerAddress,
            ServerPort = expected.ServerPort,
            UnixSocketDirectories = expected.UnixSocketDirectories,
        };
        var wrongDatabaseReport = ReadOnlyPostgres.CompareDatabaseIdentity(
            manifest,
            wrongDatabase);
        Assert.False(wrongDatabaseReport.Passed);
        Assert.Contains("database-name", wrongDatabaseReport.Failures);

        var wrongContainerAddress = new RolloutManifest
        {
            DatabaseIdentity = expected,
            ServiceDatabaseTarget = manifest.ServiceDatabaseTarget,
            PostgresContainerId = manifest.PostgresContainerId,
            PostgresImageReference = manifest.PostgresImageReference,
            PostgresImageId = manifest.PostgresImageId,
            PostgresNetworkNames = manifest.PostgresNetworkNames,
            PostgresNetworkAliases = manifest.PostgresNetworkAliases,
            PostgresServerAddresses = ["172.99.0.2"],
            PostgresNetworkBindings = manifest.PostgresNetworkBindings,
        };
        var bindingReport = ReadOnlyPostgres.CompareDatabaseIdentity(
            wrongContainerAddress,
            expected);
        Assert.False(bindingReport.Passed);
        Assert.Contains(
            "database-address-container-binding",
            bindingReport.Failures);

        var cloneOwnedAlias = new RolloutManifest
        {
            DatabaseIdentity = expected,
            ServiceDatabaseTarget = manifest.ServiceDatabaseTarget,
            PostgresContainerId = manifest.PostgresContainerId,
            PostgresImageReference = manifest.PostgresImageReference,
            PostgresImageId = manifest.PostgresImageId,
            PostgresNetworkNames = manifest.PostgresNetworkNames,
            PostgresNetworkAliases = manifest.PostgresNetworkAliases,
            PostgresServerAddresses = manifest.PostgresServerAddresses,
            PostgresNetworkBindings = TestNetworkBindings("clone-postgres"),
        };
        var cloneOwnerReport = ReadOnlyPostgres.CompareDatabaseIdentity(
            cloneOwnedAlias,
            expected);
        Assert.False(cloneOwnerReport.Passed);
        Assert.Contains(
            "exclusive-service-network-owner",
            cloneOwnerReport.Failures);

        var ambiguousAliasNetworks = new RolloutManifest
        {
            DatabaseIdentity = expected,
            ServiceDatabaseTarget = manifest.ServiceDatabaseTarget,
            PostgresContainerId = manifest.PostgresContainerId,
            PostgresImageReference = manifest.PostgresImageReference,
            PostgresImageId = manifest.PostgresImageId,
            PostgresNetworkNames = ["fst-network", "clone-network"],
            PostgresNetworkAliases = manifest.PostgresNetworkAliases,
            PostgresServerAddresses = manifest.PostgresServerAddresses,
            PostgresNetworkBindings =
            [
                .. manifest.PostgresNetworkBindings,
                new PostgresNetworkBinding
                {
                    NetworkName = "clone-network",
                    NetworkId = "clone-network-id",
                    ServiceAlias = "postgres",
                    ExclusiveOwnerContainerId = "clone-postgres",
                    ServerAddresses = ["172.20.0.99"],
                },
            ],
        };
        var ambiguousAliasReport = ReadOnlyPostgres.CompareDatabaseIdentity(
            ambiguousAliasNetworks,
            expected);
        Assert.False(ambiguousAliasReport.Passed);
        Assert.Contains(
            "exclusive-service-network-binding-count",
            ambiguousAliasReport.Failures);
    }

    [Fact]
    public void Postgres_runtime_target_exposes_only_sanitized_effective_fields()
    {
        var target = PostgresRuntimeTarget.FromConnectionString(
            "Host=postgres;Port=5432;Database=fstservice;Username=fst;" +
            "Password=do-not-render;Options=-c default_transaction_read_only=on");

        Assert.Equal("postgres", target.Host);
        Assert.Equal(5432, target.Port);
        Assert.Equal("fstservice", target.Database);
        Assert.Equal("fst", target.Username);
        Assert.True(target.DefaultTransactionReadOnlyOption);
        Assert.DoesNotContain(
            "Password",
            JsonSerializer.Serialize(target),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("do-not-render", JsonSerializer.Serialize(target));
    }

    [Fact]
    public void Manifest_fingerprint_includes_service_image_pin()
    {
        var first = new RolloutManifest
        {
            Seed = 1,
            ServiceImageReference = TestServiceImage,
            ServiceImageId = TestServiceImageId,
            WorkerContainerId = "worker-container",
            WorkerImageReference = "ghcr.io/sfenton/fstservice:worker",
            WorkerImageId =
                "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            WorkerContainerStatus = "exited",
            WorkerContainerState = "exited|start|finish|0",
        };
        var second = new RolloutManifest
        {
            Seed = 1,
            ServiceImageReference =
                "ghcr.io/sfenton/fstservice:other@sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            ServiceImageId =
                "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
        };

        Assert.NotEqual(
            DeterministicRollout.ComputeManifestFingerprint(first),
            DeterministicRollout.ComputeManifestFingerprint(second));
    }

    [Fact]
    public void Manifest_fingerprint_includes_database_runtime_binding()
    {
        var first = new RolloutManifest
        {
            Seed = 8472,
            DatabaseIdentity = TestDatabaseIdentity(),
            ServiceDatabaseTarget = TestDatabaseTarget(),
            PostgresContainerId = "production-postgres",
            PostgresImageReference = "fst-postgres:17-repack",
            PostgresImageId =
                "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
            PostgresNetworkNames = ["fst-network"],
            PostgresNetworkAliases = ["postgres"],
            PostgresServerAddresses = ["172.20.0.2"],
            PostgresNetworkBindings = TestNetworkBindings("production-postgres"),
        };
        var second = new RolloutManifest
        {
            Seed = first.Seed,
            DatabaseIdentity = first.DatabaseIdentity,
            ServiceDatabaseTarget = first.ServiceDatabaseTarget,
            PostgresContainerId = first.PostgresContainerId,
            PostgresImageReference = first.PostgresImageReference,
            PostgresImageId = first.PostgresImageId,
            PostgresNetworkNames = first.PostgresNetworkNames,
            PostgresNetworkAliases = first.PostgresNetworkAliases,
            PostgresServerAddresses = first.PostgresServerAddresses,
            PostgresNetworkBindings = TestNetworkBindings(
                "production-postgres",
                networkId: "clone-network-id"),
        };

        Assert.NotEqual(
            DeterministicRollout.ComputeManifestFingerprint(first),
            DeterministicRollout.ComputeManifestFingerprint(second));
    }

    [Fact]
    public void Final_acceptance_requires_verified_false_rollback()
    {
        var manifest = new RolloutManifest
        {
            SelectionFingerprint = "manifest",
            ServiceImageReference = TestServiceImage,
            ServiceImageId = TestServiceImageId,
            WorkerContainerId = "worker",
            WorkerImageReference = "ghcr.io/sfenton/fstservice:worker",
            WorkerImageId =
                "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            WorkerContainerStatus = "exited",
            WorkerContainerState = "exited|start|finish|0",
            DatabaseIdentity = TestDatabaseIdentity(),
            ServiceDatabaseTarget = TestDatabaseTarget(),
            PostgresContainerId = "postgres",
            PostgresImageReference = "fst-postgres:17-repack",
            PostgresImageId =
                "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
            PostgresNetworkNames = ["fst-network"],
            PostgresNetworkAliases = ["postgres", "fst-postgres"],
            PostgresServerAddresses = ["172.20.0.2"],
            PostgresNetworkBindings = TestNetworkBindings("postgres"),
        };
        var analysis = new BenchmarkAnalysisReport { Passed = true };
        var verifiedRollback = new RollbackVerificationEvidence
        {
            Label = "rollback",
            ManifestFingerprint = "manifest",
            FstserviceContainerId = "service",
            FstserviceContainerHostname = "service-host",
            FstserviceInstanceNonce = new string('a', 32),
            FstserviceBaseUrl = "http://127.0.0.1:8081",
            FstworkerContainerId = "worker",
            FstserviceImageReference = TestServiceImage,
            FstserviceImageId = TestServiceImageId,
            FstworkerImageReference = manifest.WorkerImageReference,
            FstworkerImageId = manifest.WorkerImageId,
            FstworkerContainerStatus = manifest.WorkerContainerStatus,
            FstworkerContainerState = manifest.WorkerContainerState,
            FstserviceStoredRankFlag = false,
            FstworkerStoredRankFlag = false,
            FstservicePublishedSources = true,
            FstworkerPublishedSources = false,
            FstserviceReadOnlyStartup = true,
            FstworkerReadOnlyStartup = false,
            FstservicePostgresReadOnly = true,
            FstworkerPostgresReadOnly = false,
            FstserviceDatabaseTarget = manifest.ServiceDatabaseTarget,
            FstserviceDefaultTransactionReadOnlyOption = true,
            PostgresContainerId = manifest.PostgresContainerId,
            PostgresImageReference = manifest.PostgresImageReference,
            PostgresImageId = manifest.PostgresImageId,
            PostgresNetworkNames = manifest.PostgresNetworkNames,
            PostgresNetworkAliases = manifest.PostgresNetworkAliases,
            PostgresServerAddresses = manifest.PostgresServerAddresses,
            PostgresNetworkBindings = manifest.PostgresNetworkBindings,
            HealthVerified = true,
        };
        var verifiedRecovery = new RollbackVerificationEvidence
        {
            Label = "recovery",
            ManifestFingerprint = "manifest",
            FstserviceContainerId = "service-normal",
            FstserviceContainerHostname = "service-normal-host",
            FstserviceInstanceNonce = new string('b', 32),
            FstserviceBaseUrl = "http://127.0.0.1:8081",
            FstworkerContainerId = "worker",
            FstserviceImageReference = TestServiceImage,
            FstserviceImageId = TestServiceImageId,
            FstworkerImageReference = manifest.WorkerImageReference,
            FstworkerImageId = manifest.WorkerImageId,
            FstworkerContainerStatus = manifest.WorkerContainerStatus,
            FstworkerContainerState = manifest.WorkerContainerState,
            FstserviceStoredRankFlag = false,
            FstworkerStoredRankFlag = false,
            FstservicePublishedSources = true,
            FstworkerPublishedSources = false,
            FstserviceReadOnlyStartup = false,
            FstworkerReadOnlyStartup = false,
            FstservicePostgresReadOnly = false,
            FstworkerPostgresReadOnly = false,
            FstserviceDatabaseTarget = manifest.ServiceDatabaseTarget,
            FstserviceDefaultTransactionReadOnlyOption = false,
            PostgresContainerId = manifest.PostgresContainerId,
            PostgresImageReference = manifest.PostgresImageReference,
            PostgresImageId = manifest.PostgresImageId,
            PostgresNetworkNames = manifest.PostgresNetworkNames,
            PostgresNetworkAliases = manifest.PostgresNetworkAliases,
            PostgresServerAddresses = manifest.PostgresServerAddresses,
            PostgresNetworkBindings = manifest.PostgresNetworkBindings,
            HealthVerified = true,
        };
        var finalRuntime = new RollbackVerificationEvidence
        {
            Label = "final",
            ManifestFingerprint = "manifest",
            FstserviceContainerId = "service-normal",
            FstserviceContainerHostname = "service-normal-host",
            FstserviceInstanceNonce = new string('b', 32),
            FstserviceBaseUrl = "http://127.0.0.1:8081",
            FstworkerContainerId = "worker",
            FstserviceImageReference = TestServiceImage,
            FstserviceImageId = TestServiceImageId,
            FstworkerImageReference = manifest.WorkerImageReference,
            FstworkerImageId = manifest.WorkerImageId,
            FstworkerContainerStatus = manifest.WorkerContainerStatus,
            FstworkerContainerState = manifest.WorkerContainerState,
            FstserviceStoredRankFlag = false,
            FstworkerStoredRankFlag = false,
            FstservicePublishedSources = true,
            FstworkerPublishedSources = false,
            FstserviceReadOnlyStartup = false,
            FstworkerReadOnlyStartup = false,
            FstservicePostgresReadOnly = false,
            FstworkerPostgresReadOnly = false,
            FstserviceDatabaseTarget = manifest.ServiceDatabaseTarget,
            FstserviceDefaultTransactionReadOnlyOption = false,
            PostgresContainerId = manifest.PostgresContainerId,
            PostgresImageReference = manifest.PostgresImageReference,
            PostgresImageId = manifest.PostgresImageId,
            PostgresNetworkNames = manifest.PostgresNetworkNames,
            PostgresNetworkAliases = manifest.PostgresNetworkAliases,
            PostgresServerAddresses = manifest.PostgresServerAddresses,
            PostgresNetworkBindings = manifest.PostgresNetworkBindings,
            HealthVerified = true,
        };
        var finalQuiescence = new RolloutPreflightReport
        {
            Passed = true,
            MonitoringPrivilegeAttested = true,
            CrossRoleVisibilityAttested = true,
            DatabaseAttestation = ReadOnlyPostgres.CompareDatabaseIdentity(
                manifest,
                manifest.DatabaseIdentity),
        };
        var finalHash = new string('e', 64);

        Assert.True(RolloutAcceptance.Finalize(
            manifest,
            analysis,
            verifiedRollback,
            verifiedRecovery,
            finalRuntime,
            finalQuiescence,
            finalHash).Passed);

        var driftedFinalRuntime = new RollbackVerificationEvidence
        {
            Label = finalRuntime.Label,
            ManifestFingerprint = finalRuntime.ManifestFingerprint,
            FstserviceContainerId = "same-image-recreated-service",
            FstworkerContainerId = finalRuntime.FstworkerContainerId,
            FstserviceImageReference = finalRuntime.FstserviceImageReference,
            FstserviceImageId = finalRuntime.FstserviceImageId,
            FstworkerImageReference = finalRuntime.FstworkerImageReference,
            FstworkerImageId = finalRuntime.FstworkerImageId,
            FstworkerContainerStatus = finalRuntime.FstworkerContainerStatus,
            FstworkerContainerState = finalRuntime.FstworkerContainerState,
            FstservicePublishedSources = true,
            HealthVerified = true,
        };
        var driftRejected = RolloutAcceptance.Finalize(
            manifest,
            analysis,
            verifiedRollback,
            verifiedRecovery,
            driftedFinalRuntime,
            finalQuiescence,
            finalHash);
        Assert.False(driftRejected.Passed);
        Assert.Contains(
            "final:recovery-container-identity",
            driftRejected.Failures);

        var databaseDriftedFinalRuntime = new RollbackVerificationEvidence
        {
            Label = finalRuntime.Label,
            ManifestFingerprint = finalRuntime.ManifestFingerprint,
            FstserviceContainerId = finalRuntime.FstserviceContainerId,
            FstserviceContainerHostname = finalRuntime.FstserviceContainerHostname,
            FstserviceInstanceNonce = finalRuntime.FstserviceInstanceNonce,
            FstserviceBaseUrl = finalRuntime.FstserviceBaseUrl,
            FstworkerContainerId = finalRuntime.FstworkerContainerId,
            FstserviceImageReference = finalRuntime.FstserviceImageReference,
            FstserviceImageId = finalRuntime.FstserviceImageId,
            FstworkerImageReference = finalRuntime.FstworkerImageReference,
            FstworkerImageId = finalRuntime.FstworkerImageId,
            FstworkerContainerStatus = finalRuntime.FstworkerContainerStatus,
            FstworkerContainerState = finalRuntime.FstworkerContainerState,
            FstservicePublishedSources = true,
            FstserviceDatabaseTarget = new ServiceDatabaseTarget
            {
                Host = "clone-postgres",
                Port = manifest.ServiceDatabaseTarget.Port,
                Database = manifest.ServiceDatabaseTarget.Database,
                Username = manifest.ServiceDatabaseTarget.Username,
            },
            PostgresContainerId = manifest.PostgresContainerId,
            PostgresImageReference = manifest.PostgresImageReference,
            PostgresImageId = manifest.PostgresImageId,
            PostgresNetworkNames = manifest.PostgresNetworkNames,
            PostgresNetworkAliases = manifest.PostgresNetworkAliases,
            PostgresServerAddresses = manifest.PostgresServerAddresses,
            PostgresNetworkBindings = manifest.PostgresNetworkBindings,
            HealthVerified = true,
        };
        var databaseDriftRejected = RolloutAcceptance.Finalize(
            manifest,
            analysis,
            verifiedRollback,
            verifiedRecovery,
            databaseDriftedFinalRuntime,
            finalQuiescence,
            finalHash);
        Assert.False(databaseDriftRejected.Passed);
        Assert.Contains(
            "final:service-database-target",
            databaseDriftRejected.Failures);

        var failedRollback = new RollbackVerificationEvidence
        {
            Label = verifiedRollback.Label,
            ManifestFingerprint = verifiedRollback.ManifestFingerprint,
            FstserviceContainerId = verifiedRollback.FstserviceContainerId,
            FstworkerContainerId = verifiedRollback.FstworkerContainerId,
            FstserviceImageReference = verifiedRollback.FstserviceImageReference,
            FstserviceImageId = verifiedRollback.FstserviceImageId,
            FstservicePublishedSources = true,
            FstserviceReadOnlyStartup = true,
            FstservicePostgresReadOnly = true,
            HealthVerified = false,
        };
        var rejected = RolloutAcceptance.Finalize(
            manifest,
            analysis,
            failedRollback,
            verifiedRecovery,
            finalRuntime,
            finalQuiescence,
            finalHash);
        Assert.False(rejected.Passed);
        Assert.Contains("rollback:health", rejected.Failures);

        var failedRecovery = new RollbackVerificationEvidence
        {
            Label = "recovery",
            ManifestFingerprint = "manifest",
            FstserviceContainerId = "service-normal",
            FstworkerContainerId = "worker",
            FstserviceImageReference = TestServiceImage,
            FstserviceImageId = TestServiceImageId,
            FstservicePublishedSources = true,
            FstserviceReadOnlyStartup = true,
            FstservicePostgresReadOnly = false,
            HealthVerified = true,
        };
        var recoveryRejected = RolloutAcceptance.Finalize(
            manifest,
            analysis,
            verifiedRollback,
            failedRecovery,
            finalRuntime,
            finalQuiescence,
            finalHash);
        Assert.False(recoveryRejected.Passed);
        Assert.Contains(
            "recovery:read-only-startup-not-false",
            recoveryRejected.Failures);
    }

    [Fact]
    public void Parity_comparer_detects_an_injected_rank_difference()
    {
        var baseline = new ComparableLeaderboardRow
        {
            AccountId = "account-a",
            Score = 100_000,
            Rank = 1,
            EndTime = "2026-08-04T00:00:00Z",
            Source = "projection",
        };
        var candidate = new ComparableLeaderboardRow
        {
            AccountId = "account-a",
            Score = 100_000,
            Rank = 2,
            EndTime = "2026-08-04T00:00:00Z",
            Source = "projection",
        };

        var differences = ParityComparison.CompareLeaderboard(
            1,
            [baseline],
            1,
            [candidate]);

        var difference = Assert.Single(differences);
        Assert.Equal("rank", difference.Field);
        Assert.Equal("1", difference.Baseline);
        Assert.Equal("2", difference.Candidate);
    }

    [Fact]
    public async Task Api_comparison_rejects_identical_failure_statuses_including_player()
    {
        var items = new[]
        {
            new ApiCaptureItem
            {
                WorkloadId = "single",
                Kind = "single",
                Path = "/api/single",
                ExpectedStatusCode = 200,
                StatusCode = 404,
                BodySha256 = "same-404",
            },
            new ApiCaptureItem
            {
                WorkloadId = "player",
                Kind = "player",
                Path = "/api/player/account",
                ExpectedStatusCode = 200,
                StatusCode = 500,
                BodySha256 = "same-500",
            },
        };
        var baseline = new ApiCaptureReport
        {
            Variant = "baseline",
            ManifestFingerprint = "manifest",
            UnexpectedStatusCount = 2,
            Passed = false,
            Items = items,
        };
        var candidate = new ApiCaptureReport
        {
            Variant = "candidate",
            ManifestFingerprint = "manifest",
            UnexpectedStatusCount = 2,
            Passed = false,
            Items = items,
        };

        var report = await ApiRunner.CompareAsync(
            baseline,
            ".",
            candidate,
            ".",
            CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Equal(4, report.DifferenceCount);
        Assert.Contains(
            report.Differences,
            static difference =>
                difference.Key == "single"
                && difference.Field == "baselineExpectedStatus");
        Assert.Contains(
            report.Differences,
            static difference =>
                difference.Key == "player"
                && difference.Field == "candidateExpectedStatus");
    }

    [Fact]
    public void Coverage_rejects_page_boundary_manifest_missing_offset_100()
    {
        var coverage = DeterministicRollout.BuildCoverage(
            [],
            [PageCase(99)],
            [],
            []);

        Assert.True(coverage.HasRankPageBoundary99);
        Assert.False(coverage.HasRankPageBoundary100);
        Assert.False(coverage.HasRankPageBoundary);
        Assert.Contains("rank-page-boundary:offset-100", coverage.MissingRequirements);
    }

    [Fact]
    public void Page_boundary_execution_rejects_empty_results()
    {
        var evaluation = ParityComparison.EvaluatePageBoundary(
            PageCase(99),
            baselineTotal: 200,
            baseline: [],
            candidateTotal: 200,
            candidate: []);

        Assert.False(evaluation.Evidence.Passed);
        Assert.Contains(
            evaluation.Differences,
            static difference => difference.Field == "baselineRowCount");
        Assert.Contains(
            evaluation.Differences,
            static difference => difference.Field == "candidateFirstRank");
    }

    [Fact]
    public void Page_boundary_execution_rejects_wrong_first_rank()
    {
        var wrongRank = new ComparableLeaderboardRow
        {
            AccountId = "wrong-rank",
            Rank = 99,
        };
        var evaluation = ParityComparison.EvaluatePageBoundary(
            PageCase(99),
            baselineTotal: 200,
            baseline: [wrongRank],
            candidateTotal: 200,
            candidate: [wrongRank]);

        Assert.False(evaluation.Evidence.Passed);
        Assert.Contains(
            evaluation.Differences,
            static difference => difference.Field == "baselineFirstRank");
        Assert.Contains(
            evaluation.Differences,
            static difference => difference.Field == "candidateFirstRank");
    }

    [Fact]
    public void Source_mismatch_overlay_cannot_satisfy_candidate_overlay_coverage()
    {
        var overlayRow = new ExpectedLeaderboardRow
        {
            AccountId = "overlay-account",
            Score = 100,
            Rank = 1,
            Source = "backfill",
        };
        var scope = Scope(
            "overlay-song",
            "Solo_Guitar",
            ScopeSourceClass.SourceMismatch,
            overlay: true,
            overlayRows: [overlayRow]);
        var overlayCase = new RowParityCase
        {
            Id = "overlay-case",
            ScopeId = scope.Id,
            SongId = scope.SongId,
            Instrument = scope.Instrument,
            MaxScore = int.MaxValue,
            ExpectedRows = [overlayRow],
            Tags = ["overlay-derived-row"],
        };

        var coverage = DeterministicRollout.BuildCoverage(
            [scope],
            [overlayCase],
            [],
            []);

        Assert.False(coverage.HasActiveOverlay);
        Assert.False(coverage.HasSourceMatchedOverlayRow);
        Assert.Contains("source-matched-active-overlay-row", coverage.MissingRequirements);
    }

    [Fact]
    public void Threshold_expected_count_rejects_off_by_one_injection()
    {
        var parityCase = new RowParityCase
        {
            Id = "threshold-exact",
            ExpectedTotalCount = 2,
        };
        var differences = ParityComparison.EvaluateExpectedEvidence(
            parityCase,
            baselineTotal: 1,
            baseline: [],
            candidateTotal: 1,
            candidate: []);

        Assert.Contains(
            differences,
            static difference => difference.Field == "baselineTotalCount");
        Assert.Contains(
            differences,
            static difference => difference.Field == "candidateTotalCount");
    }

    [Fact]
    public void Threshold_tags_without_actual_boundary_rows_do_not_satisfy_coverage()
    {
        var cases = new[]
        {
            new RowParityCase { Id = "minus", Tags = ["threshold-minus-one"] },
            new RowParityCase { Id = "exact", Tags = ["threshold-exact"] },
            new RowParityCase { Id = "plus", Tags = ["threshold-plus-one"] },
        };

        var coverage = DeterministicRollout.BuildCoverage([], cases, [], []);

        Assert.False(coverage.HasThresholdEdges);
        Assert.Contains(
            "threshold-minus-one-exact-plus-one",
            coverage.MissingRequirements);
    }

    [Fact]
    public void Threshold_expected_membership_rejects_off_by_one_score_injection()
    {
        var expected = new ExpectedLeaderboardRow
        {
            AccountId = "boundary-account",
            Score = 100,
            Rank = 1,
            Source = "projection",
        };
        var parityCase = new RowParityCase
        {
            Id = "threshold-exact",
            ExpectedRows = [expected],
        };
        var injected = new ComparableLeaderboardRow
        {
            AccountId = expected.AccountId,
            Score = 99,
            Rank = expected.Rank,
            Source = expected.Source,
        };
        var differences = ParityComparison.EvaluateExpectedEvidence(
            parityCase,
            baselineTotal: 1,
            baseline: [injected],
            candidateTotal: 1,
            candidate: [injected]);

        Assert.Contains(
            differences,
            static difference => difference.Field == "baselineScore");
        Assert.Contains(
            differences,
            static difference => difference.Field == "candidateScore");
    }

    [Fact]
    public void Schedule_is_randomized_ABBA_and_meets_core_sample_minimums()
    {
        var manifest = new RolloutManifest
        {
            Seed = 12345,
            ApiWorkloads =
            [
                new ApiWorkload
                {
                    Id = "core-filtered-player",
                    Kind = "member",
                    Path = "/api/player-query",
                    AccountIds = ["account"],
                    Tags = ["single-account"],
                    Core = true,
                    Benchmark = true,
                },
                new ApiWorkload
                {
                    Id = "other-list",
                    Kind = "list",
                    Path = "/api/list",
                    Benchmark = true,
                },
            ],
        };

        var schedule = DeterministicRollout.BuildSchedule(manifest, manifest.Seed);
        foreach (var group in schedule.GroupBy(static item =>
                     (item.WorkloadId, item.Mode, item.Concurrency, item.AbbaBlock)))
        {
            var variants = group.OrderBy(static item => item.Position)
                .Select(static item => item.Variant)
                .ToArray();
            Assert.True(
                variants.SequenceEqual(["baseline", "candidate", "candidate", "baseline"])
                || variants.SequenceEqual(["candidate", "baseline", "baseline", "candidate"]));
        }

        AssertSamples("cold", 1, 30);
        AssertSamples("cold", 8, 32);
        AssertSamples("warm", 1, 200);
        AssertSamples("warm", 8, 200);

        void AssertSamples(string mode, int concurrency, int expectedPerVariant)
        {
            var matching = schedule.Where(item =>
                item.WorkloadId == "core-filtered-player"
                && item.Mode == mode
                && item.Concurrency == concurrency);
            Assert.Equal(
                expectedPerVariant,
                matching.Where(static item => item.Variant == "baseline")
                    .Sum(static item => item.RequestCount));
            Assert.Equal(
                expectedPerVariant,
                matching.Where(static item => item.Variant == "candidate")
                    .Sum(static item => item.RequestCount));
        }
    }

    [Theory]
    [InlineData("512MiB", 536_870_912L)]
    [InlineData("1.5GiB", 1_610_612_736L)]
    [InlineData("2GB", 2_000_000_000L)]
    public void Resource_parser_reports_current_memory_bytes(string value, long expected)
    {
        Assert.Equal(expected, DockerStats.ParseByteSize(value));
    }

    [Fact]
    public void Benchmark_analysis_enforces_core_improvement_and_resource_limits()
    {
        var workload = new ApiWorkload
        {
            Id = "core",
            Kind = "single",
            Path = "/api/core",
            Core = true,
            Benchmark = true,
        };
        var manifest = new RolloutManifest
        {
            Seed = 2468,
            ApiWorkloads = [workload],
        };
        var blocks = DeterministicRollout.BuildSchedule(manifest, manifest.Seed)
            .Select(entry => Block(
                entry,
                entry.Variant == "candidate" && entry.Mode == "warm" ? 8.5 : 10,
                entry.Variant == "candidate" ? 9 : 10,
                entry.Variant == "candidate" ? 1_050 : 1_000,
                entry.Variant == "candidate" ? 9 : 10))
            .ToList();
        var parity = new ParityReport { Passed = true };
        var api = new ApiComparisonReport { Passed = true };

        var accepted = BenchmarkAnalyzer.Analyze(manifest, parity, api, blocks);

        Assert.True(accepted.Passed, string.Join(", ", accepted.Failures));
        Assert.All(
            blocks,
            static block => Assert.Contains(
                block.PostgresContainerSamples,
                sample =>
                    sample.ObservedAtUtc > block.HttpRequestsCompletedAtUtc
                    && sample.ObservedAtUtc <= block.RequestsCompletedAtUtc));
        Assert.Equal(
            ["cold", "warm"],
            accepted.Resources.Select(static resource => resource.Mode));
        Assert.All(accepted.Resources, static resource => Assert.True(resource.Passed));

        var regressed = blocks
            .Select(block => block.Variant == "candidate" && block.Mode == "warm"
                ? CloneLatency(block, 9.5)
                : block)
            .ToArray();
        var rejected = BenchmarkAnalyzer.Analyze(manifest, parity, api, regressed);
        Assert.False(rejected.PerformancePassed);
        Assert.Contains(rejected.Failures, failure => failure.StartsWith("p95:core:warm:", StringComparison.Ordinal));
    }

    [Fact]
    public void Benchmark_analysis_rejects_a_clone_database_attestation()
    {
        var workload = new ApiWorkload
        {
            Id = "core",
            Kind = "single",
            Path = "/api/core",
            Core = true,
            Benchmark = true,
        };
        var manifest = new RolloutManifest
        {
            Seed = 2468,
            ApiWorkloads = [workload],
            DatabaseIdentity = TestDatabaseIdentity(),
            ServiceDatabaseTarget = TestDatabaseTarget(),
            PostgresContainerId = "postgres",
            PostgresImageReference = "fst-postgres:17-repack",
            PostgresImageId =
                "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
            PostgresNetworkNames = ["fst-network"],
            PostgresNetworkAliases = ["postgres"],
            PostgresServerAddresses = ["172.20.0.2"],
            PostgresNetworkBindings = TestNetworkBindings("postgres"),
        };
        var blocks = DeterministicRollout.BuildSchedule(manifest, manifest.Seed)
            .Select(entry => Block(
                entry,
                entry.Variant == "candidate" && entry.Mode == "warm" ? 8.5 : 10,
                entry.Variant == "candidate" ? 9 : 10,
                entry.Variant == "candidate" ? 1_050 : 1_000,
                entry.Variant == "candidate" ? 9 : 10))
            .ToArray();
        var validAttestation = ReadOnlyPostgres.CompareDatabaseIdentity(
            manifest,
            manifest.DatabaseIdentity);
        foreach (var block in blocks)
            block.DatabaseAttestation = validAttestation;
        blocks[0].DatabaseAttestation = ReadOnlyPostgres.CompareDatabaseIdentity(
            manifest,
            new DatabaseIdentityEvidence
            {
                DatabaseName = manifest.DatabaseIdentity.DatabaseName,
                SystemIdentifier = "clone-system-identifier",
                ServerAddress = "172.20.0.99",
                ServerPort = manifest.DatabaseIdentity.ServerPort,
                UnixSocketDirectories =
                    manifest.DatabaseIdentity.UnixSocketDirectories,
            });

        var report = BenchmarkAnalyzer.Analyze(
            manifest,
            new ParityReport { Passed = true },
            new ApiComparisonReport { Passed = true },
            blocks);

        Assert.False(report.CorrectnessPassed);
        Assert.False(report.Passed);
        Assert.Contains(
            $"benchmark-database-attestation:{blocks[0].Sequence}",
            report.Failures);
    }

    [Fact]
    public void Benchmark_analysis_rejects_cold_only_resource_regressions()
    {
        var workload = new ApiWorkload
        {
            Id = "core",
            Kind = "single",
            Path = "/api/core",
            Core = true,
            Benchmark = true,
        };
        var manifest = new RolloutManifest
        {
            Seed = 2468,
            ApiWorkloads = [workload],
        };
        var blocks = DeterministicRollout.BuildSchedule(manifest, manifest.Seed)
            .Select(entry => Block(
                entry,
                entry.Variant == "candidate" && entry.Mode == "warm" ? 8.5 : 10,
                entry.Variant == "candidate" ? 9 : 10,
                entry.Variant == "candidate" ? 1_050 : 1_000,
                entry.Variant == "candidate" ? 9 : 10))
            .Select(block =>
                block.Mode == "cold" && block.Variant == "candidate"
                    ? CloneResources(block, cpu: 12, memory: 1_200, counters: 12)
                    : block)
            .ToArray();

        var report = BenchmarkAnalyzer.Analyze(
            manifest,
            new ParityReport { Passed = true },
            new ApiComparisonReport { Passed = true },
            blocks);

        Assert.False(report.ResourcesPassed);
        Assert.False(report.Passed);
        var cold = Assert.Single(report.Resources, static resource => resource.Mode == "cold");
        var warm = Assert.Single(report.Resources, static resource => resource.Mode == "warm");
        Assert.False(cold.Passed);
        Assert.True(warm.Passed);
        Assert.Equal(20, cold.CpuChangePercent!.Value, precision: 3);
        Assert.Equal(20, cold.MemoryChangePercent!.Value, precision: 3);
        Assert.Equal(20, cold.BlocksReadChangePercent!.Value, precision: 3);
        Assert.Equal(20, cold.TempBytesChangePercent!.Value, precision: 3);
        Assert.Equal(20, cold.TempFilesChangePercent!.Value, precision: 3);
        foreach (var metric in new[]
                 {
                     "postgres-cpu-p95:cold:",
                     "postgres-memory-current-p95:cold:",
                     "postgres-blocks-read-per-request:cold:",
                     "postgres-temp-bytes-per-request:cold:",
                     "postgres-temp-files-per-request:cold:",
                 })
        {
            Assert.Contains(
                report.Failures,
                failure => failure.StartsWith(metric, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Benchmark_analysis_rejects_fast_cold_block_with_only_post_request_sample()
    {
        var workload = new ApiWorkload
        {
            Id = "core",
            Kind = "single",
            Path = "/api/core",
            Core = true,
            Benchmark = true,
        };
        var manifest = new RolloutManifest
        {
            Seed = 2468,
            ApiWorkloads = [workload],
        };
        var blocks = DeterministicRollout.BuildSchedule(manifest, manifest.Seed)
            .Select(entry => Block(
                entry,
                entry.Variant == "candidate" && entry.Mode == "warm" ? 8.5 : 10,
                entry.Variant == "candidate" ? 9 : 10,
                entry.Variant == "candidate" ? 1_050 : 1_000,
                entry.Variant == "candidate" ? 9 : 10))
            .ToArray();
        var target = blocks.First(static block =>
            block.Mode == "cold"
            && block.Concurrency == 1
            && block.Variant == "candidate");
        blocks[Array.IndexOf(blocks, target)] = ClonePostRequestSample(target);

        var report = BenchmarkAnalyzer.Analyze(
            manifest,
            new ParityReport { Passed = true },
            new ApiComparisonReport { Passed = true },
            blocks);

        Assert.False(report.ResourcesPassed);
        Assert.False(report.Passed);
        Assert.Contains(
            report.Failures,
            failure => failure ==
                $"postgres-container-resource-samples-nonoverlapping:cold:{target.Sequence}");
        var cold = Assert.Single(report.Resources, static resource => resource.Mode == "cold");
        Assert.Equal(cold.BlockCount - 1, cold.BlocksWithOverlappingSamples);
    }

    [Fact]
    public void Benchmark_analysis_rejects_sampler_armed_after_request_start()
    {
        var workload = new ApiWorkload
        {
            Id = "core",
            Kind = "player",
            Path = "/api/player/core",
            Core = true,
            Benchmark = true,
        };
        var manifest = new RolloutManifest
        {
            Seed = 2468,
            ApiWorkloads = [workload],
        };
        var blocks = DeterministicRollout.BuildSchedule(manifest, manifest.Seed)
            .Select(entry => Block(
                entry,
                entry.Variant == "candidate" && entry.Mode == "warm" ? 8.5 : 10,
                entry.Variant == "candidate" ? 9 : 10,
                entry.Variant == "candidate" ? 1_050 : 1_000,
                entry.Variant == "candidate" ? 9 : 10))
            .ToArray();
        var target = blocks[0];
        blocks[0] = CloneSamplerArmedAt(
            target,
            target.RequestsStartedAtUtc.AddMilliseconds(1));

        var report = BenchmarkAnalyzer.Analyze(
            manifest,
            new ParityReport { Passed = true },
            new ApiComparisonReport { Passed = true },
            blocks);

        Assert.False(report.ResourcesPassed);
        Assert.Contains(
            $"postgres-sampler-not-armed:{target.Mode}:{target.Sequence}",
            report.Failures);
    }

    [Fact]
    public void Benchmark_analysis_serializes_zero_baseline_temp_increases_as_null()
    {
        var workload = new ApiWorkload
        {
            Id = "core",
            Kind = "single",
            Path = "/api/core",
            Core = true,
            Benchmark = true,
        };
        var manifest = new RolloutManifest
        {
            Seed = 2468,
            ApiWorkloads = [workload],
        };
        var blocks = DeterministicRollout.BuildSchedule(manifest, manifest.Seed)
            .Select(entry => Block(
                entry,
                entry.Variant == "candidate" && entry.Mode == "warm" ? 8.5 : 10,
                entry.Variant == "candidate" ? 9 : 10,
                entry.Variant == "candidate" ? 1_050 : 1_000,
                entry.Variant == "candidate" ? 9 : 10))
            .Select(block => CloneDatabaseCounters(
                block,
                block.DatabaseEnd.BlocksRead,
                tempBytes: 0,
                tempFiles: 0))
            .Select(block =>
                block.Mode == "cold" && block.Variant == "candidate"
                    ? CloneDatabaseCounters(
                        block,
                        block.DatabaseEnd.BlocksRead,
                        tempBytes: 1,
                        tempFiles: 1)
                    : block)
            .ToArray();

        var report = BenchmarkAnalyzer.Analyze(
            manifest,
            new ParityReport { Passed = true },
            new ApiComparisonReport { Passed = true },
            blocks);

        Assert.False(report.ResourcesPassed);
        var cold = Assert.Single(report.Resources, static resource => resource.Mode == "cold");
        Assert.True(cold.TempBytesBaselineZero);
        Assert.True(cold.TempFilesBaselineZero);
        Assert.Null(cold.TempBytesChangePercent);
        Assert.Null(cold.TempFilesChangePercent);
        Assert.Contains(
            "postgres-temp-bytes-per-request:cold:baseline-zero-increase",
            report.Failures);
        Assert.Contains(
            "postgres-temp-files-per-request:cold:baseline-zero-increase",
            report.Failures);

        var json = JsonSerializer.Serialize(report, RolloutJson.Options);
        Assert.Contains("\"tempBytesChangePercent\": null", json);
        Assert.Contains("\"tempFilesChangePercent\": null", json);
        Assert.DoesNotContain("Infinity", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.GetProperty("passed").GetBoolean());
    }

    [Fact]
    public async Task JsonFiles_atomic_write_leaves_only_complete_acceptance_file()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "test-artifacts",
            $"atomic-acceptance-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "acceptance.json");
        try
        {
            await JsonFiles.WriteAtomicAsync(
                path,
                new { passed = true },
                CancellationToken.None);

            Assert.True(File.Exists(path));
            Assert.Empty(Directory.EnumerateFiles(
                directory,
                "acceptance.json.partial-*"));
            using var document = JsonDocument.Parse(
                await File.ReadAllTextAsync(path));
            Assert.True(document.RootElement.GetProperty("passed").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static ScopeEvidence Scope(
        string songId,
        string instrument,
        ScopeSourceClass sourceClass,
        bool overlay = false,
        IReadOnlyList<ExpectedLeaderboardRow>? overlayRows = null) =>
        new()
        {
            Id = $"{instrument}:{songId}",
            SongId = songId,
            Instrument = instrument,
            PublishedScrapeId = 1278,
            SourceKind = sourceClass == ScopeSourceClass.Empty ? "empty" : "snapshot",
            SourceScrapeId = sourceClass == ScopeSourceClass.Reused ? 1277 : 1278,
            ProjectionSourceSnapshotId = sourceClass == ScopeSourceClass.Reused ? 1277 : 1278,
            ProjectionGeneration = sourceClass == ScopeSourceClass.ProjectionMissing ? null : 1,
            ProjectionScopeSourceSnapshotId = sourceClass == ScopeSourceClass.SourceMismatch
                ? 999
                : sourceClass == ScopeSourceClass.Reused ? 1277 : 1278,
            ProjectionStatus = sourceClass == ScopeSourceClass.ProjectionMissing
                ? null
                : "ready",
            PublishedRowCount = sourceClass == ScopeSourceClass.Empty ? 0 : 200,
            SourceClass = sourceClass,
            HasActiveOverlay = overlay,
            RawMaxScore = sourceClass == ScopeSourceClass.Empty ? null : 99_999,
            OverlayDerivedRows = overlayRows ?? [],
        };

    private static RowParityCase PageCase(int offset) =>
        new()
        {
            Id = $"page-{offset}",
            ScopeId = "page-scope",
            SongId = "page-song",
            Instrument = "Solo_Guitar",
            MaxScore = int.MaxValue,
            Top = 2,
            Offset = offset,
            ExpectedFirstRank = offset + 1,
            MinimumExpectedRows = 1,
            Tags = ["rank-page-boundary"],
        };

    private static DatabaseIdentityEvidence TestDatabaseIdentity() =>
        new()
        {
            DatabaseName = "fstservice",
            SystemIdentifier = "123456789",
            ServerAddress = "172.20.0.2",
            ServerPort = 5432,
            UnixSocketDirectories = "/var/run/postgresql",
        };

    private static ServiceDatabaseTarget TestDatabaseTarget() =>
        new()
        {
            Host = "postgres",
            Port = 5432,
            Database = "fstservice",
            Username = "fst",
        };

    private static IReadOnlyList<PostgresNetworkBinding> TestNetworkBindings(
        string owner,
        string networkId = "network-id") =>
        [
            new PostgresNetworkBinding
            {
                NetworkName = "fst-network",
                NetworkId = networkId,
                ServiceAlias = "postgres",
                ExclusiveOwnerContainerId = owner,
                ServerAddresses = ["172.20.0.2"],
            },
        ];

    private static RolloutManifest Manifest(
        IReadOnlyList<ScopeEvidence> scopes,
        DateTimeOffset generatedAt)
    {
        var rowCases = scopes
            .Where(static scope => scope.PublishedRowCount > 0)
            .Select(scope => new RowParityCase
            {
                Id = $"row:{scope.Id}",
                ScopeId = scope.Id,
                SongId = scope.SongId,
                Instrument = scope.Instrument,
                MaxScore = 100_098,
                RawMaxScore = 99_999,
                LeewayTenths = 1,
                Top = 100,
            })
            .ToArray();
        var workloads = new[]
        {
            new ApiWorkload { Id = "single", Kind = "single", Path = "/single" },
            new ApiWorkload { Id = "list", Kind = "list", Path = "/list" },
            new ApiWorkload { Id = "player", Kind = "player", Path = "/player" },
            new ApiWorkload { Id = "member", Kind = "member", Path = "/member" },
        };
        return new RolloutManifest
        {
            Seed = 8472,
            GeneratedAtUtc = generatedAt,
            PublishedScrapeId = 1278,
            ServiceImageReference = TestServiceImage,
            ServiceImageId = TestServiceImageId,
            EvidenceMountTarget = RolloutEvidenceMount.RequiredTarget,
            EvidenceMountSource = "/dev/test-fst",
            EvidenceMountFileSystem = "ext4",
            RequiredInstruments = GlobalLeaderboardScraper.AllInstruments,
            Scopes = scopes,
            RowCases = rowCases,
            ApiWorkloads = workloads,
            Coverage = DeterministicRollout.BuildCoverage(
                scopes,
                rowCases,
                workloads,
                GlobalLeaderboardScraper.AllInstruments),
        };
    }

    private static BenchmarkBlockReport Block(
        BenchmarkScheduleEntry entry,
        double latency,
        double cpu,
        long memory,
        long blocksRead)
    {
        var requestStart = DateTimeOffset.Parse("2026-08-04T00:00:00Z")
            .AddSeconds(entry.Sequence * 10);
        var requestEnd = requestStart.AddSeconds(1);
        return new BenchmarkBlockReport
        {
            Sequence = entry.Sequence,
            Variant = entry.Variant,
            Mode = entry.Mode,
            Concurrency = entry.Concurrency,
            WorkloadId = entry.WorkloadId,
            RequestedCount = entry.RequestCount,
            CompletedCount = entry.RequestCount,
            WarmRequestStartsPerSecond = ApiRunner.DefaultWarmRequestStartsPerSecond,
            SamplerArmedAtUtc = requestStart.AddMilliseconds(-200),
            RequestsStartedAtUtc = requestStart,
            HttpRequestsCompletedAtUtc = requestStart.AddMilliseconds(100),
            RequestsCompletedAtUtc = requestEnd,
            LatencyMilliseconds = Enumerable.Repeat(latency, entry.RequestCount).ToArray(),
            StatusCounts = new Dictionary<int, int> { [200] = entry.RequestCount },
            BodyFingerprints = ["same"],
            DatabaseStart = new DatabaseResourceSnapshot(),
            DatabaseEnd = new DatabaseResourceSnapshot
            {
                BlocksRead = blocksRead,
                TempBytes = blocksRead,
                TempFiles = blocksRead,
            },
            PostgresContainerSamples =
            [
                new ContainerResourceSample
                {
                    IntervalStartedAtUtc = requestStart.AddMilliseconds(-100),
                    IntervalCompletedAtUtc = requestEnd.AddMilliseconds(100),
                    ObservedAtUtc = requestStart.AddMilliseconds(500),
                    CpuPercent = cpu,
                    MemoryCurrentBytes = memory,
                },
            ],
        };
    }

    private static BenchmarkBlockReport CloneLatency(BenchmarkBlockReport source, double latency) =>
        new()
        {
            Sequence = source.Sequence,
            Variant = source.Variant,
            Mode = source.Mode,
            Concurrency = source.Concurrency,
            WorkloadId = source.WorkloadId,
            RequestedCount = source.RequestedCount,
            CompletedCount = source.CompletedCount,
            WarmRequestStartsPerSecond = source.WarmRequestStartsPerSecond,
            SamplerArmedAtUtc = source.SamplerArmedAtUtc,
            RequestsStartedAtUtc = source.RequestsStartedAtUtc,
            HttpRequestsCompletedAtUtc = source.HttpRequestsCompletedAtUtc,
            RequestsCompletedAtUtc = source.RequestsCompletedAtUtc,
            LatencyMilliseconds = Enumerable.Repeat(latency, source.CompletedCount).ToArray(),
            StatusCounts = source.StatusCounts,
            BodyFingerprints = source.BodyFingerprints,
            DatabaseStart = source.DatabaseStart,
            DatabaseEnd = source.DatabaseEnd,
            PostgresContainerSamples = source.PostgresContainerSamples,
        };

    private static BenchmarkBlockReport CloneResources(
        BenchmarkBlockReport source,
        double cpu,
        long memory,
        long counters) =>
        new()
        {
            Sequence = source.Sequence,
            Variant = source.Variant,
            Mode = source.Mode,
            Concurrency = source.Concurrency,
            WorkloadId = source.WorkloadId,
            RequestedCount = source.RequestedCount,
            CompletedCount = source.CompletedCount,
            WarmRequestStartsPerSecond = source.WarmRequestStartsPerSecond,
            SamplerArmedAtUtc = source.SamplerArmedAtUtc,
            RequestsStartedAtUtc = source.RequestsStartedAtUtc,
            HttpRequestsCompletedAtUtc = source.HttpRequestsCompletedAtUtc,
            RequestsCompletedAtUtc = source.RequestsCompletedAtUtc,
            LatencyMilliseconds = source.LatencyMilliseconds,
            StatusCounts = source.StatusCounts,
            BodyFingerprints = source.BodyFingerprints,
            DatabaseStart = source.DatabaseStart,
            DatabaseEnd = new DatabaseResourceSnapshot
            {
                BlocksRead = counters,
                TempBytes = counters,
                TempFiles = counters,
                StatsResetAtUtc = source.DatabaseEnd.StatsResetAtUtc,
            },
            PostgresContainerSamples =
            [
                new ContainerResourceSample
                {
                    IntervalStartedAtUtc = source.RequestsStartedAtUtc.AddMilliseconds(-100),
                    IntervalCompletedAtUtc = source.RequestsCompletedAtUtc.AddMilliseconds(100),
                    ObservedAtUtc = source.RequestsStartedAtUtc.AddMilliseconds(500),
                    CpuPercent = cpu,
                    MemoryCurrentBytes = memory,
                },
            ],
        };

    private static BenchmarkBlockReport ClonePostRequestSample(
        BenchmarkBlockReport source) =>
        new()
        {
            Sequence = source.Sequence,
            Variant = source.Variant,
            Mode = source.Mode,
            Concurrency = source.Concurrency,
            WorkloadId = source.WorkloadId,
            RequestedCount = source.RequestedCount,
            CompletedCount = source.CompletedCount,
            WarmRequestStartsPerSecond = source.WarmRequestStartsPerSecond,
            SamplerArmedAtUtc = source.SamplerArmedAtUtc,
            RequestsStartedAtUtc = source.RequestsStartedAtUtc,
            HttpRequestsCompletedAtUtc = source.HttpRequestsCompletedAtUtc,
            RequestsCompletedAtUtc = source.RequestsCompletedAtUtc,
            LatencyMilliseconds = source.LatencyMilliseconds,
            StatusCounts = source.StatusCounts,
            BodyFingerprints = source.BodyFingerprints,
            DatabaseStart = source.DatabaseStart,
            DatabaseEnd = source.DatabaseEnd,
            PostgresContainerSamples =
            [
                new ContainerResourceSample
                {
                    IntervalStartedAtUtc = source.RequestsCompletedAtUtc.AddMilliseconds(1),
                    IntervalCompletedAtUtc = source.RequestsCompletedAtUtc.AddSeconds(1),
                    ObservedAtUtc = source.RequestsCompletedAtUtc.AddSeconds(1),
                    CpuPercent = source.PostgresContainerSamples[0].CpuPercent,
                    MemoryCurrentBytes = source.PostgresContainerSamples[0].MemoryCurrentBytes,
                },
            ],
        };

    private static BenchmarkBlockReport CloneDatabaseCounters(
        BenchmarkBlockReport source,
        long blocksRead,
        long tempBytes,
        long tempFiles) =>
        new()
        {
            Sequence = source.Sequence,
            Variant = source.Variant,
            Mode = source.Mode,
            Concurrency = source.Concurrency,
            WorkloadId = source.WorkloadId,
            RequestedCount = source.RequestedCount,
            CompletedCount = source.CompletedCount,
            WarmRequestStartsPerSecond = source.WarmRequestStartsPerSecond,
            SamplerArmedAtUtc = source.SamplerArmedAtUtc,
            RequestsStartedAtUtc = source.RequestsStartedAtUtc,
            HttpRequestsCompletedAtUtc = source.HttpRequestsCompletedAtUtc,
            RequestsCompletedAtUtc = source.RequestsCompletedAtUtc,
            LatencyMilliseconds = source.LatencyMilliseconds,
            StatusCounts = source.StatusCounts,
            BodyFingerprints = source.BodyFingerprints,
            DatabaseStart = source.DatabaseStart,
            DatabaseEnd = new DatabaseResourceSnapshot
            {
                BlocksRead = blocksRead,
                TempBytes = tempBytes,
                TempFiles = tempFiles,
                StatsResetAtUtc = source.DatabaseEnd.StatsResetAtUtc,
            },
            PostgresContainerSamples = source.PostgresContainerSamples,
        };

    private static BenchmarkBlockReport CloneSamplerArmedAt(
        BenchmarkBlockReport source,
        DateTimeOffset samplerArmedAt) =>
        new()
        {
            Sequence = source.Sequence,
            Variant = source.Variant,
            Mode = source.Mode,
            Concurrency = source.Concurrency,
            WorkloadId = source.WorkloadId,
            RequestedCount = source.RequestedCount,
            CompletedCount = source.CompletedCount,
            WarmRequestStartsPerSecond = source.WarmRequestStartsPerSecond,
            SamplerArmedAtUtc = samplerArmedAt,
            RequestsStartedAtUtc = source.RequestsStartedAtUtc,
            HttpRequestsCompletedAtUtc = source.HttpRequestsCompletedAtUtc,
            RequestsCompletedAtUtc = source.RequestsCompletedAtUtc,
            LatencyMilliseconds = source.LatencyMilliseconds,
            StatusCounts = source.StatusCounts,
            BodyFingerprints = source.BodyFingerprints,
            DatabaseStart = source.DatabaseStart,
            DatabaseEnd = source.DatabaseEnd,
            PostgresContainerSamples = source.PostgresContainerSamples,
        };
}
