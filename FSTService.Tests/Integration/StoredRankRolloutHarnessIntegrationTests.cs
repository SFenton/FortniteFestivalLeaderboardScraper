using FstStoredRankRollout;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Npgsql;
using Testcontainers.PostgreSql;

namespace FSTService.Tests.Integration;

public sealed class StoredRankRolloutHarnessIntegrationTests
{
    private const string TestServiceImage =
        "ghcr.io/sfenton/fstservice:test@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string TestServiceImageId =
        "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string TestWorkerContainerId = "worker-container";
    private const string TestWorkerImage = "ghcr.io/sfenton/fstservice:worker";
    private const string TestWorkerImageId =
        "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string TestWorkerState =
        "exited|2026-08-04T00:00:00Z|2026-08-04T01:00:00Z|0";
    private const string TestMountTarget = "/mnt/docker-storage";
    private const string TestMountSource = "/dev/test-fst";
    private const string TestMountFileSystem = "ext4";

    [Fact]
    public async Task Manifest_and_row_harness_cover_the_complete_rollout_matrix()
    {
        await using var dataSource = SharedPostgresContainer.CreateDatabase();
        Seed(dataSource);
        string databaseName;
        using (var connection = dataSource.OpenConnection())
            databaseName = connection.Database;
        var serviceTarget = new NpgsqlConnectionStringBuilder(
            SharedPostgresContainer.ConnectionString)
        {
            Database = databaseName,
        };
        var readOnlyConnection = new NpgsqlConnectionStringBuilder(
            SharedPostgresContainer.ConnectionString)
        {
            Database = databaseName,
            Options =
                "-c statement_timeout=30000 " +
                "-c lock_timeout=2000 " +
                "-c idle_in_transaction_session_timeout=60000",
            MaxPoolSize = 16,
        };
        await using var readOnlyDataSource = NpgsqlDataSource.Create(readOnlyConnection.ConnectionString);
        var databaseIdentity = await ReadOnlyPostgres.ReadDatabaseIdentityAsync(
            readOnlyDataSource,
            CancellationToken.None);
        IReadOnlyList<PostgresNetworkBinding> postgresNetworkBindings =
        [
            new PostgresNetworkBinding
            {
                NetworkName = "test-network",
                NetworkId = "test-network-id",
                ServiceAlias = "postgres",
                ExclusiveOwnerContainerId = "postgres-container",
                ServerAddresses = [databaseIdentity.ServerAddress],
            },
        ];
        var generator = new ManifestGenerator(readOnlyDataSource);

        var first = await generator.GenerateAsync(
            seed: 20260804,
            maxMappedScopes: 100,
            maxTieScopesPerInstrument: 2,
            serviceImageReference: TestServiceImage,
            serviceImageId: TestServiceImageId,
            workerContainerId: TestWorkerContainerId,
            workerImageReference: TestWorkerImage,
            workerImageId: TestWorkerImageId,
            workerContainerStatus: "exited",
            workerContainerState: TestWorkerState,
            serviceDatabaseHost: "postgres",
            serviceDatabasePort: databaseIdentity.ServerPort,
            serviceDatabaseName: databaseName,
            serviceDatabaseUsername: serviceTarget.Username!,
            postgresContainerId: "postgres-container",
            postgresImageReference: "postgres:test",
            postgresImageId:
                "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
            postgresNetworkNames: ["test-network"],
            postgresNetworkAliases: ["postgres", "fst-postgres"],
            postgresServerAddresses: [databaseIdentity.ServerAddress],
            postgresNetworkBindings: postgresNetworkBindings,
            evidenceMountTarget: TestMountTarget,
            evidenceMountSource: TestMountSource,
            evidenceMountFileSystem: TestMountFileSystem,
            cancellationToken: CancellationToken.None);
        var second = await generator.GenerateAsync(
            seed: 20260804,
            maxMappedScopes: 100,
            maxTieScopesPerInstrument: 2,
            serviceImageReference: TestServiceImage,
            serviceImageId: TestServiceImageId,
            workerContainerId: TestWorkerContainerId,
            workerImageReference: TestWorkerImage,
            workerImageId: TestWorkerImageId,
            workerContainerStatus: "exited",
            workerContainerState: TestWorkerState,
            serviceDatabaseHost: "postgres",
            serviceDatabasePort: databaseIdentity.ServerPort,
            serviceDatabaseName: databaseName,
            serviceDatabaseUsername: serviceTarget.Username!,
            postgresContainerId: "postgres-container",
            postgresImageReference: "postgres:test",
            postgresImageId:
                "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
            postgresNetworkNames: ["test-network"],
            postgresNetworkAliases: ["postgres", "fst-postgres"],
            postgresServerAddresses: [databaseIdentity.ServerAddress],
            postgresNetworkBindings: postgresNetworkBindings,
            evidenceMountTarget: TestMountTarget,
            evidenceMountSource: TestMountSource,
            evidenceMountFileSystem: TestMountFileSystem,
            cancellationToken: CancellationToken.None);

        Assert.True(first.Coverage.PromotionReady, string.Join(", ", first.Coverage.MissingRequirements));
        Assert.Equal(TestServiceImage, first.ServiceImageReference);
        Assert.Equal(TestServiceImageId, first.ServiceImageId);
        Assert.Equal(TestWorkerContainerId, first.WorkerContainerId);
        Assert.Equal(TestWorkerImageId, first.WorkerImageId);
        Assert.Equal(databaseIdentity.DatabaseName, first.DatabaseIdentity.DatabaseName);
        Assert.Equal(
            databaseIdentity.SystemIdentifier,
            first.DatabaseIdentity.SystemIdentifier);
        Assert.Equal(databaseIdentity.ServerAddress, first.DatabaseIdentity.ServerAddress);
        Assert.Equal(databaseIdentity.ServerPort, first.DatabaseIdentity.ServerPort);
        Assert.Equal(
            databaseIdentity.UnixSocketDirectories,
            first.DatabaseIdentity.UnixSocketDirectories);
        Assert.Equal("postgres", first.ServiceDatabaseTarget.Host);
        Assert.Equal(databaseIdentity.ServerPort, first.ServiceDatabaseTarget.Port);
        Assert.Equal("postgres-container", first.PostgresContainerId);
        Assert.Contains("test-network", first.PostgresNetworkNames);
        Assert.Contains(databaseIdentity.ServerAddress, first.PostgresServerAddresses);
        Assert.Equal("postgres-container", Assert.Single(
            first.PostgresNetworkBindings).ExclusiveOwnerContainerId);
        Assert.Equal(TestMountSource, first.EvidenceMountSource);
        Assert.Equal(TestMountFileSystem, first.EvidenceMountFileSystem);
        Assert.Equal(GlobalLeaderboardScraper.AllInstruments.Order(), first.Coverage.CoveredInstruments.Order());
        Assert.Contains(nameof(ScopeSourceClass.Current), first.Coverage.CoveredSourceClasses);
        Assert.Contains(nameof(ScopeSourceClass.Reused), first.Coverage.CoveredSourceClasses);
        Assert.Contains(nameof(ScopeSourceClass.Empty), first.Coverage.CoveredSourceClasses);
        Assert.Contains(nameof(ScopeSourceClass.SourceMismatch), first.Coverage.CoveredSourceClasses);
        Assert.True(first.Coverage.HasActiveOverlay);
        Assert.True(first.Coverage.HasSourceMatchedOverlayRow);
        Assert.True(first.Coverage.HasExactScoreTimeTie);
        Assert.True(first.Coverage.HasRankPageBoundary99);
        Assert.True(first.Coverage.HasRankPageBoundary100);
        Assert.True(first.Coverage.HasRankPageBoundary);
        Assert.True(first.Coverage.HasThresholdEdges);
        Assert.True(first.Coverage.HasFractionalThresholdTruncation);
        Assert.Contains(
            first.ApiWorkloads,
            static workload =>
                workload.Core
                && workload.Kind == "member"
                && workload.AccountIds.Count == 1
                && workload.Tags.Contains("single-account"));
        Assert.DoesNotContain(
            first.ApiWorkloads,
            static workload => workload.Core && workload.Kind == "player");
        Assert.Contains(
            first.ApiWorkloads,
            static workload =>
                !workload.Core
                && workload.Kind == "member"
                && workload.AccountIds.Count > 1
                && workload.Tags.Contains("multi-account-parity"));
        Assert.Equal(first.SelectionFingerprint, second.SelectionFingerprint);

        var report = await new ParityRunner(readOnlyDataSource).RunAsync(first, CancellationToken.None);

        Assert.True(report.Passed);
        Assert.Equal(0, report.DifferenceCount);
        Assert.True(report.PageBoundariesPassed);
        Assert.Equal(
            [99, 100],
            report.PageBoundaries
                .Where(static evidence => evidence.Passed)
                .Select(static evidence => evidence.Offset)
                .Distinct()
                .Order());
        Assert.All(
            report.PageBoundaries,
            static evidence =>
            {
                Assert.True(evidence.Passed);
                Assert.True(evidence.BaselineRowCount > 0);
                Assert.True(evidence.CandidateRowCount > 0);
                Assert.Equal(evidence.Offset + 1, evidence.BaselineFirstRank);
                Assert.Equal(evidence.Offset + 1, evidence.CandidateFirstRank);
            });
        Assert.NotEmpty(report.Cases);
        Assert.Contains(report.Cases, result => result.Instrument == "Solo_PeripheralDrums");
        Assert.Contains(
            report.Cases,
            static result =>
                result.CaseId.Contains("source-matched-overlay-row", StringComparison.Ordinal)
                && result.Differences.Count == 0);

        using (var connection = dataSource.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE current_leaderboard_entries
                SET rank = rank + 1000
                WHERE instrument = 'Solo_Guitar'
                  AND account_id = 'rollout-account-001'
                """;
            Assert.True(command.ExecuteNonQuery() > 0);
        }
        var injectedDifference = await new ParityRunner(readOnlyDataSource)
            .RunAsync(first, CancellationToken.None);
        Assert.False(injectedDifference.Passed);
        Assert.True(injectedDifference.DifferenceCount > 0);
        Assert.Contains(
            injectedDifference.Cases.SelectMany(static result => result.Differences),
            static difference => difference.Field == "rank");

        using (var connection = dataSource.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE songs
                SET max_lead_score = max_lead_score + 1,
                    path_generation_revision = path_generation_revision + 1
                WHERE song_id LIKE 'rollout-%'
                """;
            Assert.True(command.ExecuteNonQuery() > 0);
        }
        var changedGuard = await generator.ValidateGuardAsync(first, CancellationToken.None);
        Assert.False(changedGuard.Passed);
        Assert.Contains("selection-guard-changed", changedGuard.Failures);

        using (var connection = dataSource.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE service_worker_status
                SET status = 'running',
                    last_heartbeat_at = now(),
                    current_operation_json = NULL,
                    updated_at = now()
                WHERE worker_key = 'scraper'
                """;
            command.ExecuteNonQuery();
        }
        var runningIdleWorker = await ReadOnlyPostgres.ReadPreflightAsync(
            readOnlyDataSource,
            expectedPublishedScrapeId: 50,
            CancellationToken.None);
        Assert.False(runningIdleWorker.Passed);
        Assert.Contains(
            runningIdleWorker.Failures,
            static failure => failure.StartsWith(
                "worker-ledger-not-offline-or-stale:",
                StringComparison.Ordinal));

        using (var connection = dataSource.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE service_worker_status
                SET status = 'offline',
                    last_heartbeat_at = NULL,
                    updated_at = now()
                WHERE worker_key = 'scraper'
                """;
            command.ExecuteNonQuery();
        }

        using (var workerConnection = dataSource.OpenConnection())
        {
            using (var setApplication = workerConnection.CreateCommand())
            {
                setApplication.CommandText =
                    "SELECT set_config('application_name', 'fstworker-registration', false)";
                setApplication.ExecuteScalar();
            }
            var lateWorkerConnection = await ReadOnlyPostgres.ReadPreflightAsync(
                readOnlyDataSource,
                expectedPublishedScrapeId: 50,
                CancellationToken.None);
            Assert.False(lateWorkerConnection.Passed);
            Assert.Contains(
                lateWorkerConnection.Failures,
                static failure => failure.StartsWith(
                    "active-worker-connections:",
                    StringComparison.Ordinal));
            Assert.Contains(
                "fstworker-registration",
                lateWorkerConnection.ActiveWorkerApplications);
            using var resetApplication = workerConnection.CreateCommand();
            resetApplication.CommandText =
                "SELECT set_config('application_name', 'fst-test', false)";
            resetApplication.ExecuteScalar();
        }

        using (var leaseConnection = dataSource.OpenConnection())
        {
            using (var acquire = leaseConnection.CreateCommand())
            {
                acquire.CommandText = """
                    SELECT set_config(
                        'application_name',
                        'fst-path-generation-admission',
                        false);
                    SELECT pg_advisory_lock(@lockKey);
                    """;
                acquire.Parameters.AddWithValue(
                    "lockKey",
                    PathGenerationAdmissionLock.AdvisoryLockKey);
                acquire.ExecuteNonQuery();
            }
            var grantedPathLease = await ReadOnlyPostgres.ReadPreflightAsync(
                readOnlyDataSource,
                expectedPublishedScrapeId: 50,
                CancellationToken.None);
            Assert.False(grantedPathLease.Passed);
            Assert.Contains(
                grantedPathLease.Failures,
                static failure => failure.StartsWith(
                    "granted-worker-mutation-leases:",
                    StringComparison.Ordinal));
            using var release = leaseConnection.CreateCommand();
            release.CommandText = "SELECT pg_advisory_unlock(@lockKey)";
            release.Parameters.AddWithValue(
                "lockKey",
                PathGenerationAdmissionLock.AdvisoryLockKey);
            Assert.True(Convert.ToBoolean(release.ExecuteScalar()));
            using var resetApplication = leaseConnection.CreateCommand();
            resetApplication.CommandText =
                "SELECT set_config('application_name', 'fst-test', false)";
            resetApplication.ExecuteScalar();
        }

        using (var connection = dataSource.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO backfill_status (account_id, status)
                VALUES ('hidden-registration-work', 'in_progress')
                ON CONFLICT (account_id) DO UPDATE SET status = 'in_progress'
                """;
            command.ExecuteNonQuery();
        }
        var hiddenRegistrationWork = await ReadOnlyPostgres.ReadPreflightAsync(
            readOnlyDataSource,
            expectedPublishedScrapeId: 50,
            CancellationToken.None);
        Assert.False(hiddenRegistrationWork.Passed);
        Assert.Contains(
            hiddenRegistrationWork.Failures,
            static failure => failure.StartsWith(
                "active-durable-worker-jobs:",
                StringComparison.Ordinal));
        using (var connection = dataSource.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "DELETE FROM backfill_status WHERE account_id = 'hidden-registration-work'";
            command.ExecuteNonQuery();
        }

        var privilegeError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReadOnlyPostgres.ValidateSelectTempOnlyRoleAsync(
                readOnlyDataSource,
                readOnlyDataSource,
                CancellationToken.None));
        Assert.Contains("role-is-superuser", privilegeError.Message);

        using (var connection = dataSource.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE scrape_publication_state
                SET public_reads_frozen = TRUE,
                    updated_at = now()
                WHERE id = TRUE
                """;
            command.ExecuteNonQuery();
        }
        var frozenPreflight = await ReadOnlyPostgres.ReadPreflightAsync(
            readOnlyDataSource,
            expectedPublishedScrapeId: 50,
            CancellationToken.None);
        Assert.False(frozenPreflight.Passed);
        Assert.Contains("public-reads-frozen", frozenPreflight.Failures);
    }

    [Fact]
    public async Task Monitoring_role_sees_cross_role_worker_session_and_missing_privilege_fails_closed()
    {
        await using var dataSource = SharedPostgresContainer.CreateDatabase();
        Seed(dataSource);
        string databaseName;
        using (var connection = dataSource.OpenConnection())
            databaseName = connection.Database;

        var suffix = Guid.NewGuid().ToString("N");
        var monitorRole = $"rollout_monitor_{suffix}";
        var blindRole = $"rollout_blind_{suffix}";
        var noInheritRole = $"rollout_noinherit_{suffix}";
        const string password = "rollout-test-password";
        using (var connection = dataSource.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                CREATE ROLE "{monitorRole}" LOGIN PASSWORD '{password}';
                CREATE ROLE "{blindRole}" LOGIN PASSWORD '{password}';
                CREATE ROLE "{noInheritRole}" LOGIN NOINHERIT PASSWORD '{password}';
                GRANT CONNECT, TEMPORARY ON DATABASE "{databaseName}"
                    TO "{monitorRole}", "{blindRole}", "{noInheritRole}";
                GRANT USAGE ON SCHEMA public
                    TO "{monitorRole}", "{blindRole}", "{noInheritRole}";
                GRANT SELECT ON ALL TABLES IN SCHEMA public
                    TO "{monitorRole}", "{blindRole}", "{noInheritRole}";
                GRANT pg_read_all_stats TO "{monitorRole}";
                GRANT pg_read_all_stats TO "{noInheritRole}";
                """;
            command.ExecuteNonQuery();
        }

        await using var monitorDataSource = CreateRoleDataSource(
            databaseName,
            monitorRole,
            password,
            "fst-stored-rank-rollout");
        await using var blindDataSource = CreateRoleDataSource(
            databaseName,
            blindRole,
            password,
            "fstworker-registration");
        await using var visibilityProbeDataSource = CreateRoleDataSource(
            databaseName,
            blindRole,
            password,
            "fst-stored-rank-visibility-probe");
        await using var noInheritDataSource = CreateRoleDataSource(
            databaseName,
            noInheritRole,
            password,
            "fst-noinherit-monitor");
        try
        {
            Assert.True(await ReadOnlyPostgres.ValidateSelectTempOnlyRoleAsync(
                monitorDataSource,
                visibilityProbeDataSource,
                CancellationToken.None));
            var monitorIdentity = await ReadOnlyPostgres.ReadDatabaseIdentityAsync(
                monitorDataSource,
                CancellationToken.None);
            Assert.Equal(databaseName, monitorIdentity.DatabaseName);
            Assert.NotEmpty(monitorIdentity.SystemIdentifier);

            var blindPrivilegeError = await Assert.ThrowsAsync<InvalidOperationException>(
                () => ReadOnlyPostgres.ValidateSelectTempOnlyRoleAsync(
                    blindDataSource,
                    monitorDataSource,
                    CancellationToken.None));
            Assert.Contains(
                "role-lacks-pg-monitor-or-pg-read-all-stats",
                blindPrivilegeError.Message);

            var noInheritPrivilegeError =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => ReadOnlyPostgres.ValidateSelectTempOnlyRoleAsync(
                        noInheritDataSource,
                        blindDataSource,
                        CancellationToken.None));
            Assert.Contains(
                "role-lacks-pg-monitor-or-pg-read-all-stats",
                noInheritPrivilegeError.Message);

            await using var workerConnection =
                await blindDataSource.OpenConnectionAsync();
            var monitorPreflight = await ReadOnlyPostgres.ReadPreflightAsync(
                monitorDataSource,
                expectedPublishedScrapeId: 50,
                CancellationToken.None);
            Assert.True(monitorPreflight.MonitoringPrivilegeAttested);
            Assert.True(monitorPreflight.HasPgReadAllStats);
            Assert.False(monitorPreflight.HasPgMonitor);
            Assert.True(monitorPreflight.ActiveWorkerConnectionCount > 0);
            Assert.Contains(
                "fstworker-registration",
                monitorPreflight.ActiveWorkerApplications);
            Assert.Contains(
                monitorPreflight.Failures,
                static failure => failure.StartsWith(
                    "active-worker-connections:",
                    StringComparison.Ordinal));

            var blindPreflight = await ReadOnlyPostgres.ReadPreflightAsync(
                blindDataSource,
                expectedPublishedScrapeId: 50,
                CancellationToken.None);
            Assert.False(blindPreflight.MonitoringPrivilegeAttested);
            Assert.Contains(
                "monitoring-privilege-missing",
                blindPreflight.Failures);
        }
        finally
        {
            await monitorDataSource.DisposeAsync();
            await blindDataSource.DisposeAsync();
            await visibilityProbeDataSource.DisposeAsync();
            await noInheritDataSource.DisposeAsync();
            using var connection = dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                REVOKE pg_read_all_stats FROM "{monitorRole}";
                REVOKE pg_read_all_stats FROM "{noInheritRole}";
                DROP OWNED BY "{monitorRole}";
                DROP OWNED BY "{blindRole}";
                DROP OWNED BY "{noInheritRole}";
                DROP ROLE "{monitorRole}";
                DROP ROLE "{blindRole}";
                DROP ROLE "{noInheritRole}";
                """;
            command.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task Database_identity_rejects_same_named_database_on_a_different_cluster()
    {
        await using var clone = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("fst_tests")
            .WithUsername("test")
            .WithPassword("test")
            .Build();
        await clone.StartAsync();
        await using var production =
            NpgsqlDataSource.Create(SharedPostgresContainer.ConnectionString);
        await using var cloned = NpgsqlDataSource.Create(clone.GetConnectionString());

        var productionIdentity = await ReadOnlyPostgres.ReadDatabaseIdentityAsync(
            production,
            CancellationToken.None);
        var cloneIdentity = await ReadOnlyPostgres.ReadDatabaseIdentityAsync(
            cloned,
            CancellationToken.None);
        var comparison = ReadOnlyPostgres.CompareDatabaseIdentity(
            productionIdentity,
            cloneIdentity);

        Assert.Equal(productionIdentity.DatabaseName, cloneIdentity.DatabaseName);
        Assert.NotEqual(
            productionIdentity.SystemIdentifier,
            cloneIdentity.SystemIdentifier);
        Assert.False(comparison.Passed);
        Assert.Contains("system-identifier", comparison.Failures);
    }

    [Fact]
    public async Task Manifest_bound_preflight_rejects_a_different_database_on_the_same_cluster()
    {
        await using var production = SharedPostgresContainer.CreateDatabase();
        await using var alternate = SharedPostgresContainer.CreateDatabase();
        Seed(production);
        Seed(alternate);
        var identity = await ReadOnlyPostgres.ReadDatabaseIdentityAsync(
            production,
            CancellationToken.None);
        var manifest = new RolloutManifest
        {
            PublishedScrapeId = 50,
            DatabaseIdentity = identity,
            ServiceDatabaseTarget = new ServiceDatabaseTarget
            {
                Host = "postgres",
                Port = identity.ServerPort,
                Database = identity.DatabaseName,
                Username = "fst",
            },
            PostgresContainerId = "postgres-container",
            PostgresImageReference = "postgres:17-alpine",
            PostgresImageId =
                "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
            PostgresNetworkNames = ["test-network"],
            PostgresNetworkAliases = ["postgres"],
            PostgresServerAddresses = [identity.ServerAddress],
            PostgresNetworkBindings =
            [
                new PostgresNetworkBinding
                {
                    NetworkName = "test-network",
                    NetworkId = "test-network-id",
                    ServiceAlias = "postgres",
                    ExclusiveOwnerContainerId = "postgres-container",
                    ServerAddresses = [identity.ServerAddress],
                },
            ],
        };

        var report = await ReadOnlyPostgres.ReadPreflightAsync(
            alternate,
            expectedPublishedScrapeId: 50,
            manifest: manifest,
            cancellationToken: CancellationToken.None);

        Assert.False(report.Passed);
        Assert.NotNull(report.DatabaseAttestation);
        Assert.Contains(
            "database-name",
            report.DatabaseAttestation.Failures);
        Assert.Contains(
            report.Failures,
            static failure => failure == "database-identity:database-name");
    }

    private static NpgsqlDataSource CreateRoleDataSource(
        string databaseName,
        string username,
        string password,
        string applicationName)
    {
        var builder = new NpgsqlConnectionStringBuilder(
            SharedPostgresContainer.ConnectionString)
        {
            Database = databaseName,
            Username = username,
            Password = password,
            ApplicationName = applicationName,
            Options =
                "-c statement_timeout=30000 " +
                "-c lock_timeout=2000 " +
                "-c idle_in_transaction_session_timeout=60000",
            MinPoolSize = 0,
            MaxPoolSize = 4,
        };
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    private static void Seed(NpgsqlDataSource dataSource)
    {
        ScrapeRunTestHelper.EnsureAllocated(dataSource, 40, completed: true);
        ScrapeRunTestHelper.EnsureAllocated(dataSource, 50, completed: true);
        using (var connection = dataSource.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO service_worker_status (
                    worker_key, status, last_status_change_at, updated_at)
                VALUES ('scraper', 'offline', now(), now())
                ON CONFLICT (worker_key) DO UPDATE SET
                    status = 'offline',
                    last_heartbeat_at = NULL,
                    current_operation_json = NULL,
                    last_status_change_at = now(),
                    updated_at = now();

                INSERT INTO scrape_publication_state
                    (id, published_scrape_id, published_at, public_reads_frozen, updated_at)
                VALUES (TRUE, 50, now(), FALSE, now())
                ON CONFLICT (id) DO UPDATE SET
                    published_scrape_id = EXCLUDED.published_scrape_id,
                    published_at = EXCLUDED.published_at,
                    public_reads_frozen = FALSE,
                    updated_at = EXCLUDED.updated_at
                """;
            command.ExecuteNonQuery();
        }

        foreach (var instrument in GlobalLeaderboardScraper.AllInstruments)
        {
            var songId = $"rollout-current-{instrument}";
            InsertPublishedSource(dataSource, songId, instrument, "snapshot", 50, 50, 104);
            InsertProjectionScope(dataSource, songId, instrument, sourceSnapshotId: 50, rowCount: 104);
            InsertProjectionRows(dataSource, songId, instrument);
            InsertSongStats(dataSource, songId, instrument, 99_999, 104);
            if (instrument == "Solo_Guitar")
            {
                InsertOverlayRow(
                    dataSource,
                    songId,
                    instrument,
                    "rollout-account-001",
                    100_098);
                MarkProjectionRowAsOverlayDerived(
                    dataSource,
                    songId,
                    instrument,
                    "rollout-account-001");
            }
        }

        const string reusedSong = "rollout-reused";
        InsertPublishedSource(dataSource, reusedSong, "Solo_Guitar", "snapshot", 40, 40, 104);
        InsertProjectionScope(dataSource, reusedSong, "Solo_Guitar", sourceSnapshotId: 40, rowCount: 104);
        InsertProjectionRows(dataSource, reusedSong, "Solo_Guitar");
        InsertSongStats(dataSource, reusedSong, "Solo_Guitar", 99_999, 104);

        const string emptySong = "rollout-empty";
        InsertPublishedSource(dataSource, emptySong, "Solo_Bass", "empty", null, 50, 0);
        InsertProjectionScope(dataSource, emptySong, "Solo_Bass", sourceSnapshotId: 50, rowCount: 0);

        const string mismatchSong = "rollout-source-mismatch";
        InsertPublishedSource(dataSource, mismatchSong, "Solo_Drums", "snapshot", 50, 50, 2);
        InsertProjectionScope(dataSource, mismatchSong, "Solo_Drums", sourceSnapshotId: 49, rowCount: 2);
        InsertProjectionRows(dataSource, mismatchSong, "Solo_Drums", rowCount: 2);
        InsertSnapshotRow(dataSource, mismatchSong, "Solo_Drums", "mismatch-base", 100_098, 1);
        InsertSnapshotRow(dataSource, mismatchSong, "Solo_Drums", "mismatch-other", 100_097, 2);
        InsertOverlayRow(dataSource, mismatchSong, "Solo_Drums", "mismatch-base", 100_096);
        InsertSongStats(dataSource, mismatchSong, "Solo_Drums", 99_999, 2);
    }

    private static void InsertPublishedSource(
        NpgsqlDataSource dataSource,
        string songId,
        string instrument,
        string sourceKind,
        long? sourceSnapshotId,
        long sourceScrapeId,
        int rowCount)
    {
        using var connection = dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO songs (
                song_id,
                max_lead_score,
                max_bass_score,
                max_drums_score,
                max_vocals_score,
                max_pro_lead_score,
                max_pro_bass_score)
            VALUES (
                @songId,
                99999,
                99999,
                99999,
                99999,
                99999,
                99999)
            ON CONFLICT (song_id) DO UPDATE SET
                max_lead_score = EXCLUDED.max_lead_score,
                max_bass_score = EXCLUDED.max_bass_score,
                max_drums_score = EXCLUDED.max_drums_score,
                max_vocals_score = EXCLUDED.max_vocals_score,
                max_pro_lead_score = EXCLUDED.max_pro_lead_score,
                max_pro_bass_score = EXCLUDED.max_pro_bass_score,
                path_generation_revision = songs.path_generation_revision + 1;

            INSERT INTO leaderboard_published_scope_source (
                published_scrape_id, song_id, instrument, scope_kind, source_kind,
                source_snapshot_id, source_scrape_id, row_count, content_fingerprint,
                coverage_fingerprint, reported_total_entries, reported_total_pages,
                is_complete, created_at, validated_at)
            VALUES (
                50, @songId, @instrument, 'alltime', @sourceKind,
                @sourceSnapshotId, @sourceScrapeId, @rowCount,
                md5(@songId || ':content'), md5(@songId || ':coverage'),
                @rowCount, CASE WHEN @rowCount = 0 THEN 0 ELSE 2 END,
                TRUE, now(), now())
            """;
        command.Parameters.AddWithValue("songId", songId);
        command.Parameters.AddWithValue("instrument", instrument);
        command.Parameters.AddWithValue("sourceKind", sourceKind);
        command.Parameters.AddWithValue(
            "sourceSnapshotId",
            sourceSnapshotId.HasValue ? sourceSnapshotId.Value : DBNull.Value);
        command.Parameters.AddWithValue("sourceScrapeId", sourceScrapeId);
        command.Parameters.AddWithValue("rowCount", rowCount);
        command.ExecuteNonQuery();
    }

    private static void InsertProjectionScope(
        NpgsqlDataSource dataSource,
        string songId,
        string instrument,
        long sourceSnapshotId,
        int rowCount)
    {
        using var connection = dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO solo_current_projection_scope
                (song_id, instrument, projection_generation, row_count,
                 source_snapshot_id, status, updated_at)
            VALUES (@songId, @instrument, 1, @rowCount, @sourceSnapshotId, 'ready', now())
            """;
        command.Parameters.AddWithValue("songId", songId);
        command.Parameters.AddWithValue("instrument", instrument);
        command.Parameters.AddWithValue("rowCount", rowCount);
        command.Parameters.AddWithValue("sourceSnapshotId", sourceSnapshotId);
        command.ExecuteNonQuery();
    }

    private static void InsertProjectionRows(
        NpgsqlDataSource dataSource,
        string songId,
        string instrument,
        int rowCount = 104)
    {
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO current_leaderboard_entries (
                song_id, instrument, account_id, score, accuracy, is_full_combo,
                stars, season, percentile, rank, api_rank, source, difficulty,
                end_time, first_seen_at, last_updated_at, projection_generation, computed_at)
            VALUES (
                @songId, @instrument, @accountId, @score, 95, FALSE,
                5, 34, 99.0, @rank, @rank, 'projection', 3,
                '2026-08-04T00:00:00Z', @now, @now, 1, @now)
            """;
        command.Parameters.AddWithValue("songId", songId);
        command.Parameters.AddWithValue("instrument", instrument);
        var accountId = command.Parameters.Add("accountId", NpgsqlTypes.NpgsqlDbType.Text);
        var score = command.Parameters.Add("score", NpgsqlTypes.NpgsqlDbType.Integer);
        var rank = command.Parameters.Add("rank", NpgsqlTypes.NpgsqlDbType.Integer);
        command.Parameters.AddWithValue("now", DateTime.UtcNow);
        command.Prepare();
        for (var index = 0; index < rowCount; index++)
        {
            accountId.Value = $"rollout-account-{index:D3}";
            score.Value = index == 0 ? 100_099 : index == rowCount - 1 ? 100_097 : 100_098;
            rank.Value = index + 1;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void InsertSongStats(
        NpgsqlDataSource dataSource,
        string songId,
        string instrument,
        int maxScore,
        int rowCount)
    {
        using var connection = dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO song_stats (
                song_id, instrument, entry_count, previous_entry_count,
                log_weight, max_score, computed_at)
            VALUES (@songId, @instrument, @rowCount, 0, 1.0, @maxScore, now())
            """;
        command.Parameters.AddWithValue("songId", songId);
        command.Parameters.AddWithValue("instrument", instrument);
        command.Parameters.AddWithValue("rowCount", rowCount);
        command.Parameters.AddWithValue("maxScore", maxScore);
        command.ExecuteNonQuery();
    }

    private static void InsertSnapshotRow(
        NpgsqlDataSource dataSource,
        string songId,
        string instrument,
        string accountId,
        int score,
        int rank)
    {
        using var connection = dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO leaderboard_entries_snapshot (
                snapshot_id, song_id, instrument, account_id, score, rank, api_rank,
                source, end_time, first_seen_at, last_updated_at)
            VALUES (
                50, @songId, @instrument, @accountId, @score, @rank, @rank,
                'scrape', '2026-08-04T00:00:00Z', now(), now())
            """;
        command.Parameters.AddWithValue("songId", songId);
        command.Parameters.AddWithValue("instrument", instrument);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("score", score);
        command.Parameters.AddWithValue("rank", rank);
        command.ExecuteNonQuery();
    }

    private static void InsertOverlayRow(
        NpgsqlDataSource dataSource,
        string songId,
        string instrument,
        string accountId,
        int score)
    {
        using var connection = dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO leaderboard_entries_overlay (
                song_id, instrument, account_id, score, rank, api_rank, source,
                end_time, first_seen_at, last_updated_at, source_priority, overlay_reason)
            VALUES (
                @songId, @instrument, @accountId, @score, 1, 1, 'backfill',
                '2026-08-04T00:00:00Z', now(), now(), 200, 'rollout-test')
            """;
        command.Parameters.AddWithValue("songId", songId);
        command.Parameters.AddWithValue("instrument", instrument);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("score", score);
        command.ExecuteNonQuery();
    }

    private static void MarkProjectionRowAsOverlayDerived(
        NpgsqlDataSource dataSource,
        string songId,
        string instrument,
        string accountId)
    {
        using var connection = dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE current_leaderboard_entries
            SET source = 'backfill'
            WHERE song_id = @songId
              AND instrument = @instrument
              AND account_id = @accountId
            """;
        command.Parameters.AddWithValue("songId", songId);
        command.Parameters.AddWithValue("instrument", instrument);
        command.Parameters.AddWithValue("accountId", accountId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }
}
