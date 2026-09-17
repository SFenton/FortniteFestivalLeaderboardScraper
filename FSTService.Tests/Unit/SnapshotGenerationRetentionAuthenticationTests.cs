using System.Text.Json;
using FstSnapshotGenerationRetentionReport;
using FSTService.Persistence;
using FSTService.Persistence.Maintenance;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FSTService.Tests.Unit;

public sealed partial class SnapshotGenerationRetentionPlannerTests
{
    [Fact]
    public async Task AuthenticatedDedicatedInitializerUsesOriginalCredentials()
    {
        await using var authentication = await AuthenticatedPostgresScope.CreateAsync(_fixture.DataSource);
        var sanitized = new NpgsqlConnectionStringBuilder(authentication.DataSource.ConnectionString);
        Assert.False(sanitized.PersistSecurityInfo);
        Assert.True(string.IsNullOrEmpty(sanitized.Password), "Npgsql must not expose the original password.");
        int priorBackend;
        await using (var direct = await authentication.DataSource.OpenConnectionAsync())
        {
            await authentication.RequireAuthenticatedAsync(direct);
            priorBackend = direct.ProcessID;
        }
        await using (var reconstructed = new NpgsqlConnection(authentication.DataSource.ConnectionString))
            await Assert.ThrowsAnyAsync<NpgsqlException>(() => reconstructed.OpenAsync());

        var initializerBackend = 0;
        var result = await authentication.RunSchemaCommandAsync(beforeProof: async (connection, transaction, ct) =>
        {
            await authentication.RequireAuthenticatedAsync(connection, transaction);
            initializerBackend = connection.ProcessID;
            Assert.NotEqual(priorBackend, initializerBackend);
            Assert.Equal(10, connection.ConnectionTimeout);
            var normalized = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
            Assert.False(normalized.Pooling);
            Assert.False(normalized.Multiplexing);
            Assert.False(normalized.PersistSecurityInfo);
            Assert.Equal(20, normalized.CommandTimeout);
            await using var settings = connection.CreateCommand();
            settings.Transaction = transaction;
            settings.CommandText = """
                SELECT replace(current_setting('search_path'),' ','')='pg_catalog,public'
                    AND current_setting('lock_timeout')='2s'
                    AND current_setting('statement_timeout')='15s'
                    AND current_setting('idle_in_transaction_session_timeout')='20s'
                    AND current_setting('transaction_timeout')='30s'
                """;
            Assert.True(await settings.ExecuteScalarAsync(ct) is true);
        });
        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal("schema_current", json.RootElement.GetProperty("outcome").GetString());
        Assert.True(json.RootElement.GetProperty("transactionCommitted").GetBoolean());
        Assert.Equal(2, json.RootElement.GetProperty("dmlProof").GetProperty("version").GetInt32());
        Assert.Equal(0, Scalar<long>($"SELECT count(*) FROM pg_stat_activity WHERE pid={initializerBackend}"));
        authentication.RequireSecretFree(authentication.Connections.ToString()!);
        authentication.RequireSecretFree(JsonSerializer.Serialize(authentication.Connections));
        Assert.Equal("{}", JsonSerializer.Serialize(authentication.Connections));
    }

    [Fact]
    public async Task AuthenticatedWrongPasswordRetainsSecretFreeInitializerRefusal()
    {
        await using var authentication = await AuthenticatedPostgresScope.CreateAsync(_fixture.DataSource);
        var result = await authentication.RunSchemaCommandAsync(wrongPassword: true);
        Assert.Equal(2, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal("refused", json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("retention_schema_refused", json.RootElement.GetProperty("code").GetString());
        Assert.Equal("28P01", json.RootElement.GetProperty("sqlState").GetString());
        Assert.False(json.RootElement.GetProperty("transactionCommitted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("dmlProof").ValueKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthenticatedInitializerLostAcknowledgementRemainsUncertain(bool cancellation)
    {
        Execute("DROP TABLE public.snapshot_generation_retention_worker_configuration");
        await using var authentication = await AuthenticatedPostgresScope.CreateAsync(_fixture.DataSource);
        var commits = 0;
        var result = await authentication.RunSchemaCommandAsync(
            beforeProof: (connection, transaction, _) => authentication.RequireAuthenticatedAsync(connection, transaction),
            afterServerCommit: _ =>
            {
                commits++;
                if (cancellation)
                    throw new OperationCanceledException("injected acknowledgement failure");
                throw new NpgsqlException("injected acknowledgement failure");
            });
        Assert.Equal(1, commits);
        Assert.Equal(2, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal("uncertain", json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("commit_acknowledgement_unknown", json.RootElement.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("transactionCommitted").ValueKind);
        Assert.Equal(2, json.RootElement.GetProperty("dmlProof").GetProperty("version").GetInt32());
        Assert.Equal(json.RootElement.GetProperty("dmlProof").GetProperty("sha256").GetString(),
            json.RootElement.GetProperty("possibleSchemaProof").GetProperty("combinedProofSha256").GetString());
        await using var verification = authentication.Connections.CreateConnection();
        await verification.OpenAsync();
        await authentication.RequireAuthenticatedAsync(verification);
        await using var command = verification.CreateCommand();
        command.CommandText = "SELECT to_regclass('public.snapshot_generation_retention_worker_configuration') IS NOT NULL";
        Assert.True(await command.ExecuteScalarAsync() is true);
    }

    [Fact]
    public async Task AuthenticatedInitializerAcknowledgedCommitSurvivesCleanupFaultHonestly()
    {
        await using var authentication = await AuthenticatedPostgresScope.CreateAsync(_fixture.DataSource);
        var result = await authentication.RunSchemaCommandAsync(
            beforeProof: (connection, transaction, _) => authentication.RequireAuthenticatedAsync(connection, transaction),
            beforeConnectionDispose: () => throw new IOException("injected connection cleanup failure"));
        Assert.Equal(2, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal("committed_cleanup_unconfirmed", json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("post_commit_cleanup_failed", json.RootElement.GetProperty("code").GetString());
        Assert.True(json.RootElement.GetProperty("transactionCommitted").GetBoolean());
        Assert.Equal(2, json.RootElement.GetProperty("dmlProof").GetProperty("version").GetInt32());
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("planner-cleanup")]
    [InlineData("source-disposal")]
    public async Task AuthenticatedOfflineInspectObserveAndCleanupReachEveryBoundary(string completion)
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        RejectOfflineSourceMutations();
        var before = OfflineSourceSnapshot();
        await using var authentication = await AuthenticatedPostgresScope.CreateAsync(_fixture.DataSource);
        await using var database = authentication.CreateReportDatabase();
        Assert.True(string.IsNullOrEmpty(new NpgsqlConnectionStringBuilder(database.DataSource.ConnectionString).Password));
        var code = new OfflineFixtureCode();
        var identity = await database.InspectAsync(code, CancellationToken.None);
        Assert.True(identity.RequiredSchemaAccepted);
        var oracleBackend = 0;
        var fenceBackend = 0;
        var planner = database.CreatePlanner(true, new OfflineHookOracle(async (connection, transaction, _) =>
        {
            await authentication.RequireAuthenticatedAsync(connection, transaction);
            oracleBackend = connection.ProcessID;
            await using var settings = connection.CreateCommand();
            settings.Transaction = transaction;
            settings.CommandText = """
                SELECT current_setting('row_security')='off'
                    AND current_setting('lock_timeout')='2s'
                    AND current_setting('statement_timeout')='15s'
                    AND current_setting('idle_in_transaction_session_timeout')='20s'
                    AND current_setting('transaction_timeout')='2min'
                """;
            Assert.True(await settings.ExecuteScalarAsync() is true);
        }));
        planner.OfflineFenceAdmittedTestHook = async (connection, transaction, _) =>
        {
            await authentication.RequireAuthenticatedAsync(connection, transaction);
            fenceBackend = connection.ProcessID;
            await using var settings = connection.CreateCommand();
            settings.Transaction = transaction;
            settings.CommandText = """
                SELECT replace(current_setting('search_path'),' ','')='pg_catalog,public'
                    AND current_setting('row_security')='off'
                    AND current_setting('lock_timeout')='2s'
                    AND current_setting('statement_timeout')='15s'
                    AND current_setting('idle_in_transaction_session_timeout')='2min'
                    AND current_setting('transaction_timeout')='2min'
                """;
            Assert.True(await settings.ExecuteScalarAsync() is true);
        };
        if (completion == "planner-cleanup")
            planner.OfflinePostCommitCleanupTestHook = () => throw new IOException("injected planner cleanup failure");
        if (completion == "source-disposal")
            database.DisposeTestHook = () => throw new IOException("injected data source cleanup failure");
        var attestation = new OfflineReportAttestation(code, OfflineReportCommandTests.Assertions(identity));
        var observed = await planner.ObserveCurrentOfflineAsync(attestation);
        observed = await database.CompleteAndDisposeAsync(observed);
        Assert.NotEqual(0, oracleBackend);
        Assert.NotEqual(0, fenceBackend);
        Assert.NotEqual(oracleBackend, fenceBackend);
        Assert.Equal(2, observed.Completion!.Owners.Count);
        Assert.Equal(SnapshotGenerationRetentionPlanDisposition.Observed, observed.Result.Disposition);
        Assert.True(observed.Cycle.OracleAgreement);
        Assert.Equal(1, observed.Cycle.CandidateCount);
        Assert.Equal(0, observed.Cycle.BlockedCount);
        Assert.Contains(observed.Timings, item => item.Phase == "transactional_admission");
        Assert.Contains(observed.Timings, item => item.Phase == "persistence");
        if (completion != "normal")
            Assert.Contains("committed_cycle_cleanup_warning_verified", observed.Warnings);
        Assert.Equal(0, Scalar<long>($"SELECT count(*) FROM pg_stat_activity WHERE pid IN ({oracleBackend},{fenceBackend})"));
        Assert.Equal(before, OfflineSourceSnapshot());
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM snapshot_generation_retention_cycles"));
        authentication.RequireSecretFree(JsonSerializer.Serialize(observed));
        authentication.RequireSecretFree(JsonSerializer.Serialize(attestation.ObservedIdentity));

        await using var repeatedDatabase = authentication.CreateReportDatabase();
        var repeated = await repeatedDatabase.CreatePlanner(true).ObserveCurrentOfflineAsync(
            new OfflineReportAttestation(code, OfflineReportCommandTests.Assertions(identity)));
        Assert.Equal(SnapshotGenerationRetentionPlanDisposition.Existing, repeated.Result.Disposition);
        Assert.Equal(observed.Cycle, repeated.Cycle);
    }

    [Fact]
    public async Task AuthenticatedOfflineFenceRejectsRlsHiddenRetentionHold()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        Execute("""
            INSERT INTO snapshot_generation_retention_holds
                (instrument, snapshot_id, hold_kind, reason, created_by)
            VALUES ('Solo_Guitar', 1307, 'restore_in_flight', 'RLS fixture', 'test');
            ALTER TABLE snapshot_generation_retention_holds ENABLE ROW LEVEL SECURITY;
            """);
        await using var authentication = await AuthenticatedPostgresScope.CreateAsync(_fixture.DataSource);
        await authentication.EnforceRowSecurityAsync();
        await using (var filtered = authentication.Connections.CreateConnection())
        {
            await filtered.OpenAsync();
            await using var query = filtered.CreateCommand();
            query.CommandText = "SELECT count(*) FROM snapshot_generation_retention_holds";
            Assert.Equal(0L, await query.ExecuteScalarAsync());
        }
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM snapshot_generation_retention_holds"));
        await using var database = authentication.CreateReportDatabase();
        var planner = database.CreatePlanner(true);
        var fenceReached = false;
        planner.OfflineFenceAdmittedTestHook = async (connection, transaction, ct) =>
        {
            fenceReached = true;
            await authentication.RequireAuthenticatedAsync(connection, transaction);
            await using var query = connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = "SELECT count(*) FROM snapshot_generation_retention_holds";
            await query.ExecuteScalarAsync(ct);
        };
        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            planner.ObserveCurrentOfflineAsync(new OfflineTestAttestation()));
        Assert.True(fenceReached);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, failure.SqlState);
        Assert.Contains("row-level security", failure.MessageText, StringComparison.Ordinal);
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM snapshot_generation_retention_cycles"));
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM snapshot_generation_retention_holds"));
        authentication.RequireSecretFree(failure.ToString());
    }

    [Fact]
    public async Task AuthenticatedOfflineReconciliationPreservesBoundsAndRejectsRlsHiddenCycle()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        await using var authentication = await AuthenticatedPostgresScope.CreateAsync(_fixture.DataSource);
        await using var database = authentication.CreateReportDatabase();
        var result = await database.CreatePlanner(true).ObserveCurrentOfflineAsync(new OfflineTestAttestation());
        await authentication.EnforceRowSecurityAsync();
        Assert.NotNull(await SnapshotGenerationRetentionPlanner.ConfirmCommittedAfterCleanupAsync(
            authentication.Connections, result));

        Execute("ALTER TABLE snapshot_generation_retention_cycles ENABLE ROW LEVEL SECURITY");
        await using (var filtered = authentication.Connections.CreateConnection())
        {
            await filtered.OpenAsync();
            await using var query = filtered.CreateCommand();
            query.CommandText = "SELECT count(*) FROM snapshot_generation_retention_cycles";
            Assert.Equal(0L, await query.ExecuteScalarAsync());
        }
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM snapshot_generation_retention_cycles"));
        await using var connection =
            SnapshotGenerationRetentionPlanner.CreateOfflineCommitReconciliationConnection(authentication.Connections);
        await connection.OpenAsync();
        await authentication.RequireAuthenticatedAsync(connection);
        Assert.Equal(5, connection.ConnectionTimeout);
        Assert.Equal(5, new NpgsqlConnectionStringBuilder(connection.ConnectionString).CommandTimeout);
        await using var settings = connection.CreateCommand();
        settings.CommandText = """
            SELECT replace(current_setting('search_path'),' ','')='pg_catalog,public'
                AND current_setting('row_security')='off'
                AND current_setting('lock_timeout')='2s'
                AND current_setting('statement_timeout')='5s'
                AND current_setting('transaction_timeout')='15s'
            """;
        Assert.True(await settings.ExecuteScalarAsync() is true);
        settings.CommandText = "SELECT count(*) FROM snapshot_generation_retention_cycles";
        var failure = await Assert.ThrowsAsync<PostgresException>(() => settings.ExecuteScalarAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, failure.SqlState);
        Assert.Contains("row-level security", failure.MessageText, StringComparison.Ordinal);
        Assert.Null(await SnapshotGenerationRetentionPlanner.ConfirmCommittedAfterCleanupAsync(
            authentication.Connections, result));
        authentication.RequireSecretFree(failure.ToString());
    }

    [Fact]
    public async Task AuthenticatedOfflineReconciliationRequiresExactCycleAndEndedOwnership()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        await using var authentication = await AuthenticatedPostgresScope.CreateAsync(_fixture.DataSource);
        await using var database = authentication.CreateReportDatabase();
        var result = await database.CreatePlanner(true).ObserveCurrentOfflineAsync(new OfflineTestAttestation());
        await using var connection = authentication.Connections.CreateConnection();
        await connection.OpenAsync();
        await authentication.RequireAuthenticatedAsync(connection);
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pid,backend_start,xact_start FROM pg_stat_activity WHERE pid=pg_backend_pid()";
        SnapshotGenerationRetentionTransactionOwner owner;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            owner = new(reader.GetInt32(0), reader.GetDateTime(1), reader.GetDateTime(2));
        }
        var pending = result with { Completion = result.Completion! with { Owners = [owner] } };
        Assert.Null(await SnapshotGenerationRetentionPlanner.ConfirmCommittedAfterCleanupAsync(
            authentication.Connections, pending));
        await transaction.CommitAsync();
        Assert.NotNull(await SnapshotGenerationRetentionPlanner.ConfirmCommittedAfterCleanupAsync(
            authentication.Connections, pending));
        Assert.Null(await SnapshotGenerationRetentionPlanner.ConfirmCommittedAfterCleanupAsync(
            authentication.WrongConnections(), result));
        Assert.Null(await SnapshotGenerationRetentionPlanner.ConfirmCommittedAfterCleanupAsync(
            authentication.Connections, result with { Cycle = result.Cycle with { CandidateIdentityHash = new('f', 64) } }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Null(await SnapshotGenerationRetentionPlanner.ConfirmCommittedAfterCleanupAsync(
            authentication.Connections, result, cancellation.Token));
        authentication.RequireSecretFree(JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthenticatedOfflineUnverifiableCommitNeverClaimsSuccess(bool sourceDisposal)
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        await using var authentication = await AuthenticatedPostgresScope.CreateAsync(_fixture.DataSource);
        await using var database = authentication.CreateReportDatabase();
        var planner = database.CreatePlanner(true);
        async Task FailCleanupAsync()
        {
            await authentication.RefuseNewConnectionsAsync();
            throw new IOException("injected cleanup and connection refusal");
        }
        if (sourceDisposal)
            database.DisposeTestHook = FailCleanupAsync;
        else
            planner.OfflinePostCommitCleanupTestHook = FailCleanupAsync;
        var failure = await Assert.ThrowsAsync<SnapshotGenerationRetentionOfflineRefusal>(async () =>
        {
            var observation = await planner.ObserveCurrentOfflineAsync(new OfflineTestAttestation());
            await database.CompleteAndDisposeAsync(observation);
        });
        Assert.Equal("commit_outcome_uncertain", failure.Code);
        Assert.True(failure.PossibleCommittedCycleId > 0);
        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM snapshot_generation_retention_cycles"));
        authentication.RequireSecretFree(failure.ToString());
    }

    [Fact]
    public async Task AuthenticatedOfflineBudgetStillRollsBack()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        await using var authentication = await AuthenticatedPostgresScope.CreateAsync(_fixture.DataSource);
        await using var database = authentication.CreateReportDatabase();
        var planner = database.CreatePlanner(true, new BlockingOracle());
        planner.OfflineObservationBudget = TimeSpan.FromMilliseconds(250);
        var failure = await Assert.ThrowsAsync<SnapshotGenerationRetentionOfflineRefusal>(
            () => planner.ObserveCurrentOfflineAsync(new OfflineTestAttestation()));
        Assert.Equal("observation_budget_exceeded", failure.Code);
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM snapshot_generation_retention_cycles"));
        authentication.RequireSecretFree(failure.ToString());
    }

    [Fact]
    public async Task OfflineExecutionWithoutDedicatedFactoryRefusesBeforeOpeningAConnection()
    {
        var planner = new SnapshotGenerationRetentionPlanner(
            _fixture.DataSource, new SnapshotGenerationRetentionRepository(_fixture.DataSource),
            new SnapshotGenerationRetentionOracle(), new ServiceMaintenanceLock(),
            Options.Create(new DatabaseMaintenanceOptions { SnapshotGenerationRetentionReportOnlyEnabled = true }),
            Options.Create(new ScraperOptions()), NullLogger<SnapshotGenerationRetentionPlanner>.Instance);
        var failure = await Assert.ThrowsAsync<SnapshotGenerationRetentionOfflineRefusal>(
            () => planner.ObserveCurrentOfflineAsync(new OfflineTestAttestation()));
        Assert.Equal("dedicated_connection_factory_required", failure.Code);
    }
}
