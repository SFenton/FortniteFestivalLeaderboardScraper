using System.Text.Json;
using FSTService.Persistence;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Tests.Unit;

public sealed class ScoreHistoryDedupMaintenanceServiceTests : IDisposable
{
    private const string Instrument = "Solo_Guitar";
    private readonly InMemoryMetaDatabase _fixture = new();
    private readonly ScoreHistoryDedupMaintenanceService _maintenance;

    public ScoreHistoryDedupMaintenanceServiceTests()
    {
        _maintenance = new ScoreHistoryDedupMaintenanceService(
            _fixture.DataSource,
            NullLogger<ScoreHistoryDedupMaintenanceService>.Instance);
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void CommandParserDefaultsToDryRunAndRequiresBothExecuteGates()
    {
        var digest = new string('a', 64);
        Assert.Null(ScoreHistoryDedupMaintenanceCommand.Parse([]));

        var dryRun = ScoreHistoryDedupMaintenanceCommand.Parse(
            [ScoreHistoryDedupMaintenanceCommand.MaintenanceFlag]);
        Assert.NotNull(dryRun);
        Assert.False(dryRun.Execute);
        Assert.Null(dryRun.ExpectedDigest);

        Assert.Throws<ArgumentException>(() =>
            ScoreHistoryDedupMaintenanceCommand.Parse(
            [
                ScoreHistoryDedupMaintenanceCommand.MaintenanceFlag,
                ScoreHistoryDedupMaintenanceCommand.ExecuteFlag,
            ]));
        Assert.Throws<ArgumentException>(() =>
            ScoreHistoryDedupMaintenanceCommand.Parse(
            [
                ScoreHistoryDedupMaintenanceCommand.MaintenanceFlag,
                ScoreHistoryDedupMaintenanceCommand.ExpectedDigestFlag,
                digest,
            ]));
        Assert.Throws<ArgumentException>(() =>
            ScoreHistoryDedupMaintenanceCommand.Parse(
            [
                ScoreHistoryDedupMaintenanceCommand.ExecuteFlag,
                ScoreHistoryDedupMaintenanceCommand.ExpectedDigestFlag,
                digest,
            ]));
        Assert.Throws<ArgumentException>(() =>
            ScoreHistoryDedupMaintenanceCommand.Parse(
            [
                ScoreHistoryDedupMaintenanceCommand.MaintenanceFlag,
                ScoreHistoryDedupMaintenanceCommand.MaintenanceFlag,
            ]));
        Assert.Throws<ArgumentException>(() =>
            ScoreHistoryDedupMaintenanceCommand.Parse(
            [
                ScoreHistoryDedupMaintenanceCommand.MaintenanceFlag,
                ScoreHistoryDedupMaintenanceCommand.ExecuteFlag,
                ScoreHistoryDedupMaintenanceCommand.ExecuteFlag,
                ScoreHistoryDedupMaintenanceCommand.ExpectedDigestFlag,
                digest,
            ]));
        Assert.Throws<ArgumentException>(() =>
            ScoreHistoryDedupMaintenanceCommand.Parse(
            [
                ScoreHistoryDedupMaintenanceCommand.MaintenanceFlag,
                ScoreHistoryDedupMaintenanceCommand.ExecuteFlag,
                ScoreHistoryDedupMaintenanceCommand.ExpectedDigestFlag,
            ]));
        Assert.Throws<ArgumentException>(() =>
            ScoreHistoryDedupMaintenanceCommand.Parse(
            [
                ScoreHistoryDedupMaintenanceCommand.MaintenanceFlag,
                ScoreHistoryDedupMaintenanceCommand.ExecuteFlag,
                ScoreHistoryDedupMaintenanceCommand.ExpectedDigestFlag,
                "not-a-digest",
            ]));
        Assert.Throws<ArgumentException>(() =>
            ScoreHistoryDedupMaintenanceCommand.Parse(
            [
                ScoreHistoryDedupMaintenanceCommand.MaintenanceFlag,
                ScoreHistoryDedupMaintenanceCommand.ExecuteFlag,
                ScoreHistoryDedupMaintenanceCommand.ExpectedDigestFlag,
                digest,
                ScoreHistoryDedupMaintenanceCommand.ExpectedDigestFlag,
                digest,
            ]));

        var execute = ScoreHistoryDedupMaintenanceCommand.Parse(
        [
            ScoreHistoryDedupMaintenanceCommand.MaintenanceFlag.ToUpperInvariant(),
            ScoreHistoryDedupMaintenanceCommand.ExecuteFlag.ToUpperInvariant(),
            ScoreHistoryDedupMaintenanceCommand.ExpectedDigestFlag
                .ToUpperInvariant(),
            new string('A', 64),
        ]);
        Assert.NotNull(execute);
        Assert.True(execute.Execute);
        Assert.Equal(digest, execute.ExpectedDigest);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("missing-run")]
    [InlineData("inexact")]
    [InlineData("disabled-trigger")]
    [InlineData("qualified-trigger")]
    [InlineData("column-filtered-trigger")]
    [InlineData("mutated-function")]
    public async Task AuditSchemaPreflightFailsClosed(string schemaState)
    {
        using (var conn = _fixture.DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = schemaState switch
            {
                "missing" => """
                    DROP TABLE public.score_history_dedup_original_rows
                    CASCADE;
                    """,
                "missing-run" => """
                    DROP TABLE public.score_history_dedup_maintenance_runs
                    CASCADE;
                    """,
                "inexact" => """
                    ALTER TABLE public.score_history_dedup_maintenance_runs
                    ADD COLUMN unexpected_column INTEGER;
                    """,
                "disabled-trigger" => """
                    ALTER TABLE public.score_history_dedup_maintenance_runs
                    DISABLE TRIGGER
                        trg_reject_score_history_dedup_run_mutation;
                    """,
                "qualified-trigger" => """
                    DROP TRIGGER
                        trg_reject_score_history_dedup_run_mutation
                    ON public.score_history_dedup_maintenance_runs;
                    CREATE TRIGGER
                        trg_reject_score_history_dedup_run_mutation
                    BEFORE UPDATE OR DELETE OR TRUNCATE
                    ON public.score_history_dedup_maintenance_runs
                    FOR EACH STATEMENT
                    WHEN (false)
                    EXECUTE FUNCTION
                        reject_score_history_dedup_audit_mutation();
                    """,
                "column-filtered-trigger" => """
                    DROP TRIGGER
                        trg_reject_score_history_dedup_run_mutation
                    ON public.score_history_dedup_maintenance_runs;
                    CREATE TRIGGER
                        trg_reject_score_history_dedup_run_mutation
                    BEFORE UPDATE OF rollback_sql OR DELETE OR TRUNCATE
                    ON public.score_history_dedup_maintenance_runs
                    FOR EACH STATEMENT
                    EXECUTE FUNCTION
                        reject_score_history_dedup_audit_mutation();
                    """,
                "mutated-function" => """
                    CREATE OR REPLACE FUNCTION
                        reject_score_history_dedup_audit_mutation()
                    RETURNS trigger
                    LANGUAGE plpgsql
                    AS $$
                    BEGIN
                        RETURN NULL;
                    END
                    $$;
                    """,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(schemaState)),
            };
            cmd.ExecuteNonQuery();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _maintenance.DryRunAsync());

        Assert.Contains(
            "audit schema preflight failed",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "normal release schema initialization owns it",
            exception.Message,
            StringComparison.Ordinal);

        var executeException =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => _maintenance.ExecuteAsync(new string('a', 64)));
        Assert.Contains(
            "audit schema preflight failed",
            executeException.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing", "missing")]
    [InlineData("malformed", "unexpected")]
    public async Task MissingOrMalformedDedupIndexBlocksMaintenance(
        string setup,
        string expectedState)
    {
        SeedValidDuplicateGroups();
        var before = ReadHistoryRows();
        using (var conn = _fixture.DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = setup == "missing"
                ? "DROP INDEX public.ix_sh_dedup;"
                : """
                    DROP INDEX public.ix_sh_dedup;
                    CREATE INDEX ix_sh_dedup
                    ON public.score_history (account_id);
                    """;
            cmd.ExecuteNonQuery();
        }

        var report = await _maintenance.DryRunAsync();

        Assert.Equal(expectedState, report.Index.State);
        Assert.False(report.CanExecute);
        Assert.Equal("blocked_index_invariant", report.SafetyDecision);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _maintenance.ExecuteAsync(report.DryRunDigest));
        Assert.Contains(
            "blocked_index_invariant",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(before, ReadHistoryRows());
        Assert.Equal(0, CountRows("score_history_dedup_maintenance_runs"));
        Assert.Equal(0, CountRows("score_history_dedup_original_rows"));
    }

    [Fact]
    public async Task DryRunIsDeterministicRepeatableReadAndWritesNothing()
    {
        SeedValidDuplicateGroups();
        await DatabaseInitializer.EnsureSchemaAsync(_fixture.DataSource);
        Assert.Equal(5, ReadHistoryRows().Count);
        Assert.False(IndexNullsNotDistinct());
        var before = ReadHistoryRows();

        var first = await _maintenance.DryRunAsync();
        var second = await _maintenance.DryRunAsync();

        Assert.Equal(
            JsonSerializer.Serialize(first),
            JsonSerializer.Serialize(second));
        Assert.Equal(first.DryRunDigest, second.DryRunDigest);
        Assert.Equal(64, first.DryRunDigest.Length);
        Assert.Equal("repeatable_read", first.Transaction.IsolationLevel);
        Assert.True(first.Transaction.ReadOnly);
        Assert.Equal("ready", first.SafetyDecision);
        Assert.True(first.CanExecute);
        Assert.Equal("merge_and_replace_index", first.RequiredAction);
        Assert.Equal(5, first.TotalScoreHistoryRowCount);
        Assert.Equal(5, first.NullScoreAchievedAtRowCount);
        Assert.Equal(5, first.DuplicateRowCount);
        Assert.Equal(2, first.DuplicateGroupCount);
        Assert.Equal(3, first.ExcessRowCount);
        Assert.Equal(2, first.AffectedAccountCount);
        Assert.Equal(2, first.AffectedSongCount);
        Assert.Equal(
            new[] { "account-a", "account-b" },
            first.AffectedAccounts);
        Assert.Equal(new[] { "song-a", "song-b" }, first.AffectedSongs);
        Assert.Equal("legacy_nulls_distinct", first.Index.State);
        Assert.False(first.Index.NullsNotDistinct);
        Assert.True(first.Index.SizeBytes > 0);
        Assert.True(first.Storage.TotalRelationSizeBytes > 0);
        Assert.True(first.CanonicalDataByteCount > 0);
        Assert.Contains(
            "relation_and_index_size_bytes",
            first.DigestExcludes);
        Assert.All(first.PerGroupMaxima, group =>
        {
            Assert.True(group.Allowed);
            Assert.Equal(
                "expected_zero_score_rank_metadata_only",
                group.Classification);
            Assert.Empty(group.VariedInvariantFields);
        });
        Assert.Equal(3, first.Maxima.MaximumRowsInGroup);
        Assert.Equal(2, first.Maxima.MaximumExcessRowsInGroup);
        Assert.Equal("lowest_id", first.MergeSemantics.Survivor);
        Assert.Equal("earliest_changed_at", first.MergeSemantics.ChangedAt);
        Assert.Equal(before, ReadHistoryRows());
        Assert.Equal(0, CountRows("score_history_dedup_maintenance_runs"));
        Assert.Equal(0, CountRows("score_history_dedup_original_rows"));
    }

    [Fact]
    public async Task UnexpectedScoreOrSemanticVarianceBlocksExecute()
    {
        InsertHistoryRow(
            "account-nonzero",
            "song-nonzero",
            newScore: 7,
            newRank: 2,
            allTimeRank: 2,
            changedAt: Utc(1));
        InsertHistoryRow(
            "account-nonzero",
            "song-nonzero",
            newScore: 7,
            newRank: 1,
            allTimeRank: 1,
            changedAt: Utc(2));
        InsertHistoryRow(
            "account-variance",
            "song-variance",
            newScore: 0,
            newRank: 2,
            allTimeRank: 2,
            changedAt: Utc(3),
            accuracy: 99);
        InsertHistoryRow(
            "account-variance",
            "song-variance",
            newScore: 0,
            newRank: 1,
            allTimeRank: 1,
            changedAt: Utc(4),
            accuracy: 98);
        var before = ReadHistoryRows();

        var report = await _maintenance.DryRunAsync();

        Assert.False(report.CanExecute);
        Assert.Equal("blocked_unexpected_history", report.SafetyDecision);
        Assert.Contains(
            report.PerGroupMaxima,
            group => group.Classification == "blocked_non_zero_score");
        var variance = Assert.Single(
            report.PerGroupMaxima,
            group => group.Classification == "blocked_semantic_variance");
        Assert.Equal(new[] { "accuracy" }, variance.VariedInvariantFields);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _maintenance.ExecuteAsync(report.DryRunDigest));
        Assert.Contains("blocked_unexpected_history", exception.Message);
        Assert.Equal(before, ReadHistoryRows());
        Assert.Equal(0, CountRows("score_history_dedup_maintenance_runs"));
        Assert.Equal(0, CountRows("score_history_dedup_original_rows"));
        Assert.False(IndexNullsNotDistinct());
    }

    [Fact]
    public async Task DigestMismatchFailsBeforeAnyWrite()
    {
        SeedValidDuplicateGroups();
        var before = ReadHistoryRows();
        var report = await _maintenance.DryRunAsync();
        var wrongDigest =
            (report.DryRunDigest[0] == '0' ? "1" : "0")
            + report.DryRunDigest[1..];

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _maintenance.ExecuteAsync(wrongDigest));

        Assert.Contains("no rows were written", exception.Message);
        Assert.Equal(before, ReadHistoryRows());
        Assert.Equal(0, CountRows("score_history_dedup_maintenance_runs"));
        Assert.Equal(0, CountRows("score_history_dedup_original_rows"));
        Assert.False(IndexNullsNotDistinct());
    }

    [Fact]
    public async Task CandidateDataChangeRejectsStaleDigestBeforeAnyWrite()
    {
        SeedValidDuplicateGroups();
        var dryRun = await _maintenance.DryRunAsync();
        InsertHistoryRow(
            "account-a",
            "song-a",
            newScore: 0,
            newRank: 2,
            allTimeRank: 11,
            changedAt: Utc(6));
        var changedRows = ReadHistoryRows();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _maintenance.ExecuteAsync(dryRun.DryRunDigest));

        Assert.Contains(
            "dry-run digest changed",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(changedRows, ReadHistoryRows());
        Assert.Equal(0, CountRows("score_history_dedup_maintenance_runs"));
        Assert.Equal(0, CountRows("score_history_dedup_original_rows"));
        Assert.False(IndexNullsNotDistinct());
    }

    [Fact]
    public async Task ExecuteLocksTableBeforeSnapshotAndSeesCommittedWriter()
    {
        SeedValidDuplicateGroups();
        var dryRun = await _maintenance.DryRunAsync();
        await using var writerConnection =
            await _fixture.DataSource.OpenConnectionAsync();
        await using var writerTransaction =
            await writerConnection.BeginTransactionAsync();
        var writerId = await InsertHistoryRowAsync(
            writerConnection,
            writerTransaction,
            "account-a",
            "song-a",
            newScore: 0,
            newRank: 2,
            allTimeRank: 11,
            changedAt: Utc(6));

        var executeTask = _maintenance.ExecuteAsync(dryRun.DryRunDigest);
        try
        {
            await WaitForPendingMaintenanceTableLockAsync();
            Assert.False(executeTask.IsCompleted);
            await writerTransaction.CommitAsync();
        }
        catch
        {
            if (writerTransaction.Connection is not null)
                await writerTransaction.RollbackAsync();
            try
            {
                await executeTask;
            }
            catch
            {
            }
            throw;
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => executeTask);

        Assert.Contains(
            "dry-run digest changed",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(ReadHistoryRows(), row => row.Id == writerId);
        Assert.Equal(0, CountRows("score_history_dedup_maintenance_runs"));
        Assert.Equal(0, CountRows("score_history_dedup_original_rows"));
        Assert.False(IndexNullsNotDistinct());
    }

    [Fact]
    public async Task ExecuteMergesExactlyAuditsEveryRowAndRollbackRestores()
    {
        SeedValidDuplicateGroups();
        var originalRows = ReadHistoryRows();
        var dryRun = await _maintenance.DryRunAsync();
        var accountAPlan = Assert.Single(
            dryRun.PerGroupMaxima,
            group => group.AccountId == "account-a");
        var accountBPlan = Assert.Single(
            dryRun.PerGroupMaxima,
            group => group.AccountId == "account-b");
        Assert.Equal(3, accountAPlan.SelectedNewRank);
        Assert.Equal(12, accountAPlan.SelectedAllTimeRank);
        Assert.Equal(0, accountBPlan.SelectedNewRank);
        Assert.Null(accountBPlan.SelectedAllTimeRank);

        var executed = await _maintenance.ExecuteAsync(
            dryRun.DryRunDigest);

        Assert.NotNull(executed.MaintenanceRunId);
        Assert.False(executed.AlreadyApplied);
        Assert.False(executed.NoChangesRequired);
        Assert.Equal(5, executed.OriginalRowsAudited);
        Assert.Equal(2, executed.DuplicateGroupsMerged);
        Assert.Equal(2, executed.SurvivorRowsUpdated);
        Assert.Equal(3, executed.RowsDeleted);
        Assert.True(executed.IndexReplaced);
        Assert.Equal("nulls_not_distinct", executed.IndexStateAfter);
        Assert.NotNull(executed.RollbackSql);
        Assert.Contains(
            executed.MaintenanceRunId!.Value.ToString(),
            executed.RollbackSql);
        Assert.Contains("DROP INDEX public.ix_sh_dedup", executed.RollbackSql);
        Assert.Contains(
            "CREATE UNIQUE INDEX ix_sh_dedup",
            executed.RollbackSql);

        var mergedRows = ReadHistoryRows();
        Assert.Equal(2, mergedRows.Count);
        var mergedA = Assert.Single(
            mergedRows,
            row => row.AccountId == "account-a");
        Assert.Equal(accountAPlan.SurvivorId, mergedA.Id);
        Assert.Equal(3, mergedA.NewRank);
        Assert.Equal(12, mergedA.AllTimeRank);
        Assert.Equal(Utc(1), mergedA.ChangedAt);
        var mergedB = Assert.Single(
            mergedRows,
            row => row.AccountId == "account-b");
        Assert.Equal(accountBPlan.SurvivorId, mergedB.Id);
        Assert.Equal(0, mergedB.NewRank);
        Assert.Null(mergedB.AllTimeRank);
        Assert.Equal(Utc(4), mergedB.ChangedAt);
        Assert.Equal(
            originalRows,
            ReadAuditRows(executed.MaintenanceRunId.Value));
        AssertAuditProvenance(
            executed.MaintenanceRunId.Value,
            dryRun.DryRunDigest);
        Assert.True(IndexNullsNotDistinct());
        Assert.Contains(
            "NULLS NOT DISTINCT",
            ReadIndexDefinition(),
            StringComparison.OrdinalIgnoreCase);

        var repeated = await _maintenance.ExecuteAsync(
            dryRun.DryRunDigest);
        Assert.True(repeated.AlreadyApplied);
        Assert.Equal(
            executed.MaintenanceRunId,
            repeated.MaintenanceRunId);
        Assert.Equal(1, CountRows("score_history_dedup_maintenance_runs"));

        AssertAuditIsImmutable(executed.MaintenanceRunId.Value);

        using (var conn = _fixture.DataSource.OpenConnection())
        using (var rollback = conn.CreateCommand())
        {
            rollback.CommandTimeout = 185;
            rollback.CommandText = executed.RollbackSql;
            rollback.ExecuteNonQuery();
        }

        Assert.Equal(originalRows, ReadHistoryRows());
        Assert.False(IndexNullsNotDistinct());
        Assert.DoesNotContain(
            "NULLS NOT DISTINCT",
            ReadIndexDefinition(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, CountRows("score_history_dedup_maintenance_runs"));
        Assert.Equal(5, CountRows("score_history_dedup_original_rows"));
        var restoredDryRun = await _maintenance.DryRunAsync();
        Assert.Equal(dryRun.DryRunDigest, restoredDryRun.DryRunDigest);

        var rerun = await _maintenance.ExecuteAsync(dryRun.DryRunDigest);
        Assert.False(rerun.AlreadyApplied);
        Assert.NotEqual(executed.MaintenanceRunId, rerun.MaintenanceRunId);
        Assert.Equal(2, CountRows("score_history_dedup_maintenance_runs"));
        Assert.Equal(10, CountRows("score_history_dedup_original_rows"));
        Assert.True(IndexNullsNotDistinct());
        var repeatedRerun = await _maintenance.ExecuteAsync(
            dryRun.DryRunDigest);
        Assert.True(repeatedRerun.AlreadyApplied);
        Assert.Equal(rerun.MaintenanceRunId, repeatedRerun.MaintenanceRunId);
    }

    [Fact]
    public async Task RollbackRefusesReusedNonSurvivorIdWithoutDeletingIt()
    {
        SeedValidDuplicateGroups();
        var originalRows = ReadHistoryRows();
        var dryRun = await _maintenance.DryRunAsync();
        var executed = await _maintenance.ExecuteAsync(
            dryRun.DryRunDigest);
        var survivorIds = dryRun.PerGroupMaxima
            .Select(group => group.SurvivorId)
            .ToHashSet();
        var reusedId = originalRows
            .Select(row => row.Id)
            .First(id => !survivorIds.Contains(id));
        InsertHistoryRowWithId(
            reusedId,
            "later-account",
            "later-song",
            newScore: 900,
            newRank: 1,
            allTimeRank: 1,
            changedAt: Utc(20));

        var exception = ExecuteRollbackExpectFailure(
            executed.RollbackSql!);

        Assert.Contains(
            "non-survivor score-history ID has been reused",
            exception.Message,
            StringComparison.Ordinal);
        var reused = Assert.Single(
            ReadHistoryRows(),
            row => row.Id == reusedId);
        Assert.Equal("later-account", reused.AccountId);
        Assert.True(IndexNullsNotDistinct());
        Assert.Equal(1, CountRows("score_history_dedup_maintenance_runs"));
        Assert.Equal(5, CountRows("score_history_dedup_original_rows"));
    }

    [Fact]
    public async Task RollbackPreservesUnrelatedLaterWrites()
    {
        SeedValidDuplicateGroups();
        var originalRows = ReadHistoryRows();
        var dryRun = await _maintenance.DryRunAsync();
        var executed = await _maintenance.ExecuteAsync(
            dryRun.DryRunDigest);
        var laterId = InsertHistoryRow(
            "later-account",
            "later-song",
            newScore: 777,
            newRank: 4,
            allTimeRank: 4,
            changedAt: Utc(20));
        var laterRow = Assert.Single(
            ReadHistoryRows(),
            row => row.Id == laterId);

        ExecuteRollback(executed.RollbackSql!);

        Assert.Equal(
            originalRows.Append(laterRow).OrderBy(row => row.Id),
            ReadHistoryRows());
        Assert.False(IndexNullsNotDistinct());
    }

    [Fact]
    public async Task RollbackRefusesChangedSurvivor()
    {
        SeedValidDuplicateGroups();
        var dryRun = await _maintenance.DryRunAsync();
        var executed = await _maintenance.ExecuteAsync(
            dryRun.DryRunDigest);
        var survivorId = dryRun.PerGroupMaxima[0].SurvivorId;
        using (var conn = _fixture.DataSource.OpenConnection())
        using (var update = conn.CreateCommand())
        {
            update.CommandText = """
                UPDATE public.score_history
                SET new_rank = new_rank + 1
                WHERE id = @survivorId;
                """;
            update.Parameters.AddWithValue("survivorId", survivorId);
            Assert.Equal(1, update.ExecuteNonQuery());
        }
        var changedSurvivor = Assert.Single(
            ReadHistoryRows(),
            row => row.Id == survivorId);

        var exception = ExecuteRollbackExpectFailure(
            executed.RollbackSql!);

        Assert.Contains(
            "survivor rows no longer match",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(
            changedSurvivor,
            Assert.Single(
                ReadHistoryRows(),
                row => row.Id == survivorId));
        Assert.True(IndexNullsNotDistinct());
    }

    [Fact]
    public async Task RollbackNeverRewindsSequenceAndNextValueIsSafe()
    {
        SeedValidDuplicateGroups();
        var dryRun = await _maintenance.DryRunAsync();
        var executed = await _maintenance.ExecuteAsync(
            dryRun.DryRunDigest);
        SetScoreHistorySequenceNextValue(50_000);

        ExecuteRollback(executed.RollbackSql!);

        var firstId = InsertHistoryRow(
            "sequence-account-a",
            "sequence-song-a",
            newScore: 500,
            newRank: 1,
            allTimeRank: 1,
            changedAt: Utc(21));
        var secondId = InsertHistoryRow(
            "sequence-account-b",
            "sequence-song-b",
            newScore: 501,
            newRank: 1,
            allTimeRank: 1,
            changedAt: Utc(22));
        Assert.Equal(50_000, firstId);
        Assert.Equal(50_001, secondId);
    }

    [Fact]
    public async Task ExecuteLeavesNonNullTimestampRowsUntouched()
    {
        SeedValidDuplicateGroups();
        var timestamp = Utc(15);
        var nonNullId = InsertHistoryRow(
            "account-a",
            "song-a",
            newScore: 0,
            newRank: 99,
            allTimeRank: 99,
            changedAt: Utc(16),
            scoreAchievedAt: timestamp);
        var before = Assert.Single(
            ReadHistoryRows(),
            row => row.Id == nonNullId);
        var dryRun = await _maintenance.DryRunAsync();

        var executed = await _maintenance.ExecuteAsync(
            dryRun.DryRunDigest);

        Assert.NotNull(executed.MaintenanceRunId);
        Assert.Equal(
            before,
            Assert.Single(
                ReadHistoryRows(),
                row => row.Id == nonNullId));
        Assert.DoesNotContain(
            ReadAuditRows(executed.MaintenanceRunId.Value),
            row => row.Id == nonNullId);
    }

    [Fact]
    public async Task NullConflictUpsertsAreIdempotentAcrossRepositoryPaths()
    {
        var indexDryRun = await _maintenance.DryRunAsync();
        Assert.Equal("replace_index_only", indexDryRun.RequiredAction);
        var indexExecute = await _maintenance.ExecuteAsync(
            indexDryRun.DryRunDigest);
        Assert.True(indexExecute.IndexReplaced);
        Assert.True(IndexNullsNotDistinct());

        _fixture.Db.InsertScoreChange(
            "direct-song",
            Instrument,
            "direct-account",
            oldScore: null,
            newScore: 100,
            oldRank: null,
            newRank: 5);
        _fixture.Db.InsertScoreChange(
            "direct-song",
            Instrument,
            "direct-account",
            oldScore: null,
            newScore: 100,
            oldRank: null,
            newRank: 4);

        var small = new ScoreChangeRecord
        {
            SongId = "small-song",
            Instrument = Instrument,
            AccountId = "small-account",
            NewScore = 200,
            NewRank = 3,
        };
        _fixture.Db.InsertScoreChanges([small]);
        _fixture.Db.InsertScoreChanges([small]);

        var large = Enumerable.Range(0, 21)
            .Select(index => new ScoreChangeRecord
            {
                SongId = $"large-song-{index:D2}",
                Instrument = Instrument,
                AccountId = "large-account",
                NewScore = 1_000 + index,
                NewRank = index + 1,
            })
            .ToArray();
        _fixture.Db.InsertScoreChanges(large);
        _fixture.Db.InsertScoreChanges(large);

        const int reconstructionVersion = 2;
        const string fingerprint = "score-history-null-conflict";
        var admissionRevision = _fixture.Db.AdmitHistoryRecon(
            "staged-account",
            totalSongsToProcess: 1,
            reconstructionVersion,
            fingerprint);
        var staged = new ScoreChangeRecord
        {
            SongId = "staged-song",
            Instrument = Instrument,
            AccountId = "staged-account",
            NewScore = 300,
            NewRank = 1,
        };
        Assert.True(_fixture.Db.CommitStagedHistoryData(
            "staged-account",
            [staged],
            [],
            reconstructionVersion,
            fingerprint,
            admissionRevision));
        Assert.True(_fixture.Db.CommitStagedHistoryData(
            "staged-account",
            [staged],
            [],
            reconstructionVersion,
            fingerprint,
            admissionRevision));

        Assert.Equal(1, CountHistoryKey(
            "direct-account",
            "direct-song",
            100));
        Assert.Equal(1, CountHistoryKey(
            "small-account",
            "small-song",
            200));
        Assert.Equal(21, CountHistoryAccount("large-account"));
        Assert.Equal(1, CountHistoryKey(
            "staged-account",
            "staged-song",
            300));
        Assert.Equal(0, CountDuplicateNaturalKeys());

        await DatabaseInitializer.EnsureSchemaAsync(_fixture.DataSource);
        Assert.True(IndexNullsNotDistinct());

        var rerunDryRun = await _maintenance.DryRunAsync();
        Assert.Equal("none", rerunDryRun.RequiredAction);
        var runCountBefore = CountRows(
            "score_history_dedup_maintenance_runs");
        var rerunExecute = await _maintenance.ExecuteAsync(
            rerunDryRun.DryRunDigest);
        Assert.True(rerunExecute.NoChangesRequired);
        Assert.Equal(
            runCountBefore,
            CountRows("score_history_dedup_maintenance_runs"));
    }

    private void SeedValidDuplicateGroups()
    {
        InsertHistoryRow(
            "account-a",
            "song-a",
            newScore: 0,
            newRank: 0,
            allTimeRank: null,
            changedAt: Utc(3));
        InsertHistoryRow(
            "account-a",
            "song-a",
            newScore: 0,
            newRank: 8,
            allTimeRank: 0,
            changedAt: Utc(1));
        InsertHistoryRow(
            "account-a",
            "song-a",
            newScore: 0,
            newRank: 3,
            allTimeRank: 12,
            changedAt: Utc(2));
        InsertHistoryRow(
            "account-b",
            "song-b",
            newScore: 0,
            newRank: 0,
            allTimeRank: null,
            changedAt: Utc(5));
        InsertHistoryRow(
            "account-b",
            "song-b",
            newScore: 0,
            newRank: 0,
            allTimeRank: null,
            changedAt: Utc(4));
    }

    private int InsertHistoryRow(
        string accountId,
        string songId,
        int? newScore,
        int? newRank,
        int? allTimeRank,
        DateTime changedAt,
        int? accuracy = 99,
        DateTime? scoreAchievedAt = null)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO score_history (
                song_id,
                instrument,
                account_id,
                old_score,
                new_score,
                old_rank,
                new_rank,
                accuracy,
                is_full_combo,
                stars,
                percentile,
                season,
                score_achieved_at,
                season_rank,
                all_time_rank,
                difficulty,
                changed_at)
            VALUES (
                @songId,
                @instrument,
                @accountId,
                0,
                @newScore,
                100,
                @newRank,
                @accuracy,
                FALSE,
                5,
                99.5,
                10,
                @scoreAchievedAt,
                50,
                @allTimeRank,
                6,
                @changedAt)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", Instrument);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.Add(
            "newScore",
            NpgsqlDbType.Integer).Value =
            newScore.HasValue ? newScore.Value : DBNull.Value;
        cmd.Parameters.Add(
            "newRank",
            NpgsqlDbType.Integer).Value =
            newRank.HasValue ? newRank.Value : DBNull.Value;
        cmd.Parameters.Add(
            "accuracy",
            NpgsqlDbType.Integer).Value =
            accuracy.HasValue ? accuracy.Value : DBNull.Value;
        cmd.Parameters.Add(
            "scoreAchievedAt",
            NpgsqlDbType.TimestampTz).Value =
            scoreAchievedAt.HasValue
                ? scoreAchievedAt.Value
                : DBNull.Value;
        cmd.Parameters.Add(
            "allTimeRank",
            NpgsqlDbType.Integer).Value =
            allTimeRank.HasValue ? allTimeRank.Value : DBNull.Value;
        cmd.Parameters.AddWithValue("changedAt", changedAt);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static async Task<int> InsertHistoryRowAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string accountId,
        string songId,
        int? newScore,
        int? newRank,
        int? allTimeRank,
        DateTime changedAt)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO score_history (
                song_id,
                instrument,
                account_id,
                old_score,
                new_score,
                old_rank,
                new_rank,
                accuracy,
                is_full_combo,
                stars,
                percentile,
                season,
                score_achieved_at,
                season_rank,
                all_time_rank,
                difficulty,
                changed_at)
            VALUES (
                @songId,
                @instrument,
                @accountId,
                0,
                @newScore,
                100,
                @newRank,
                99,
                FALSE,
                5,
                99.5,
                10,
                NULL,
                50,
                @allTimeRank,
                6,
                @changedAt)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", Instrument);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.Add(
            "newScore",
            NpgsqlDbType.Integer).Value =
            newScore.HasValue ? newScore.Value : DBNull.Value;
        cmd.Parameters.Add(
            "newRank",
            NpgsqlDbType.Integer).Value =
            newRank.HasValue ? newRank.Value : DBNull.Value;
        cmd.Parameters.Add(
            "allTimeRank",
            NpgsqlDbType.Integer).Value =
            allTimeRank.HasValue ? allTimeRank.Value : DBNull.Value;
        cmd.Parameters.AddWithValue("changedAt", changedAt);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private void InsertHistoryRowWithId(
        int id,
        string accountId,
        string songId,
        int newScore,
        int newRank,
        int allTimeRank,
        DateTime changedAt)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO score_history (
                id,
                song_id,
                instrument,
                account_id,
                old_score,
                new_score,
                old_rank,
                new_rank,
                accuracy,
                is_full_combo,
                stars,
                percentile,
                season,
                score_achieved_at,
                season_rank,
                all_time_rank,
                difficulty,
                changed_at)
            VALUES (
                @id,
                @songId,
                @instrument,
                @accountId,
                0,
                @newScore,
                100,
                @newRank,
                99,
                FALSE,
                5,
                99.5,
                10,
                NULL,
                50,
                @allTimeRank,
                6,
                @changedAt);
            """;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", Instrument);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("newScore", newScore);
        cmd.Parameters.AddWithValue("newRank", newRank);
        cmd.Parameters.AddWithValue("allTimeRank", allTimeRank);
        cmd.Parameters.AddWithValue("changedAt", changedAt);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    private async Task WaitForPendingMaintenanceTableLockAsync()
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_locks lock_state
                    WHERE lock_state.locktype = 'relation'
                      AND lock_state.relation =
                          'public.score_history'::regclass
                      AND lock_state.mode = 'ShareRowExclusiveLock'
                      AND NOT lock_state.granted);
                """;
            if (await cmd.ExecuteScalarAsync() is true)
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        throw new TimeoutException(
            "Execute did not wait on the score_history table lock.");
    }

    private PostgresException ExecuteRollbackExpectFailure(
        string rollbackSql)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var rollback = conn.CreateCommand();
        rollback.CommandTimeout = 185;
        rollback.CommandText = rollbackSql;
        return Assert.Throws<PostgresException>(
            () => rollback.ExecuteNonQuery());
    }

    private void ExecuteRollback(string rollbackSql)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var rollback = conn.CreateCommand();
        rollback.CommandTimeout = 185;
        rollback.CommandText = rollbackSql;
        rollback.ExecuteNonQuery();
    }

    private void SetScoreHistorySequenceNextValue(int nextValue)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT setval(
                'public.score_history_id_seq',
                @nextValue,
                FALSE);
            """;
        cmd.Parameters.AddWithValue("nextValue", nextValue);
        Assert.Equal(nextValue, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    private IReadOnlyList<HistoryRowSnapshot> ReadHistoryRows()
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                id,
                song_id,
                instrument,
                account_id,
                old_score,
                new_score,
                old_rank,
                new_rank,
                accuracy,
                is_full_combo,
                stars,
                percentile,
                season,
                score_achieved_at,
                season_rank,
                all_time_rank,
                difficulty,
                changed_at
            FROM score_history
            ORDER BY id;
            """;
        return ReadSnapshots(cmd);
    }

    private IReadOnlyList<HistoryRowSnapshot> ReadAuditRows(long runId)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                original_id,
                song_id,
                instrument,
                account_id,
                old_score,
                new_score,
                old_rank,
                new_rank,
                accuracy,
                is_full_combo,
                stars,
                percentile,
                season,
                score_achieved_at,
                season_rank,
                all_time_rank,
                difficulty,
                changed_at
            FROM score_history_dedup_original_rows
            WHERE maintenance_run_id = @runId
            ORDER BY original_id;
            """;
        cmd.Parameters.AddWithValue("runId", runId);
        return ReadSnapshots(cmd);
    }

    private static IReadOnlyList<HistoryRowSnapshot> ReadSnapshots(
        NpgsqlCommand cmd)
    {
        var rows = new List<HistoryRowSnapshot>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new HistoryRowSnapshot(
                Id: reader.GetInt32(0),
                SongId: reader.GetString(1),
                Instrument: reader.GetString(2),
                AccountId: reader.GetString(3),
                OldScore: GetNullableInt(reader, 4),
                NewScore: GetNullableInt(reader, 5),
                OldRank: GetNullableInt(reader, 6),
                NewRank: GetNullableInt(reader, 7),
                Accuracy: GetNullableInt(reader, 8),
                IsFullCombo: GetNullableBool(reader, 9),
                Stars: GetNullableInt(reader, 10),
                Percentile:
                    reader.IsDBNull(11) ? null : reader.GetFloat(11),
                Season: GetNullableInt(reader, 12),
                ScoreAchievedAt:
                    reader.IsDBNull(13)
                        ? null
                        : NormalizeUtc(reader.GetDateTime(13)),
                SeasonRank: GetNullableInt(reader, 14),
                AllTimeRank: GetNullableInt(reader, 15),
                Difficulty: GetNullableInt(reader, 16),
                ChangedAt: NormalizeUtc(reader.GetDateTime(17))));
        }

        return rows;
    }

    private void AssertAuditIsImmutable(long runId)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using (var update = conn.CreateCommand())
        {
            update.CommandText = """
                UPDATE score_history_dedup_maintenance_runs
                SET rollback_sql = 'mutated'
                WHERE maintenance_run_id = @runId;
                """;
            update.Parameters.AddWithValue("runId", runId);
            var exception = Assert.Throws<PostgresException>(
                () => update.ExecuteNonQuery());
            Assert.Equal("55000", exception.SqlState);
        }

        using (var delete = conn.CreateCommand())
        {
            delete.CommandText = """
                DELETE FROM score_history_dedup_original_rows
                WHERE maintenance_run_id = @runId;
                """;
            delete.Parameters.AddWithValue("runId", runId);
            var exception = Assert.Throws<PostgresException>(
                () => delete.ExecuteNonQuery());
            Assert.Equal("55000", exception.SqlState);
        }

        using (var append = conn.CreateCommand())
        {
            append.CommandText = """
                INSERT INTO score_history_dedup_original_rows (
                    maintenance_run_id,
                    original_id,
                    song_id,
                    instrument,
                    account_id,
                    old_score,
                    new_score,
                    old_rank,
                    new_rank,
                    accuracy,
                    is_full_combo,
                    stars,
                    percentile,
                    season,
                    score_achieved_at,
                    season_rank,
                    all_time_rank,
                    difficulty,
                    changed_at)
                SELECT
                    maintenance_run_id,
                    original_id + 1000000,
                    song_id,
                    instrument,
                    account_id,
                    old_score,
                    new_score,
                    old_rank,
                    new_rank,
                    accuracy,
                    is_full_combo,
                    stars,
                    percentile,
                    season,
                    score_achieved_at,
                    season_rank,
                    all_time_rank,
                    difficulty,
                    changed_at
                FROM score_history_dedup_original_rows
                WHERE maintenance_run_id = @runId
                ORDER BY original_id
                LIMIT 1;
                """;
            append.Parameters.AddWithValue("runId", runId);
            var exception = Assert.Throws<PostgresException>(
                () => append.ExecuteNonQuery());
            Assert.Equal("55000", exception.SqlState);
        }
    }

    private void AssertAuditProvenance(long runId, string digest)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                maintenance_purpose,
                maintenance_contract_version,
                execution_source,
                dry_run_digest,
                length(canonical_candidate_data) > 0,
                length(database_name) > 0,
                length(database_user) > 0,
                server_version_num >= 170000,
                executed_at IS NOT NULL,
                length(rollback_sql) > 0
            FROM score_history_dedup_maintenance_runs
            WHERE maintenance_run_id = @runId;
            """;
        cmd.Parameters.AddWithValue("runId", runId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(
            ScoreHistoryDedupMaintenanceSchema.Purpose,
            reader.GetString(0));
        Assert.Equal(
            ScoreHistoryDedupMaintenanceSchema.ContractVersion,
            reader.GetInt32(1));
        Assert.Equal(
            ScoreHistoryDedupMaintenanceSchema.ExecutionSource,
            reader.GetString(2));
        Assert.Equal(digest, reader.GetString(3));
        for (var ordinal = 4; ordinal <= 9; ordinal++)
            Assert.True(reader.GetBoolean(ordinal));
    }

    private int CountRows(string tableName)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "score_history_dedup_maintenance_runs",
            "score_history_dedup_original_rows",
        };
        Assert.Contains(tableName, allowed);
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private int CountHistoryKey(
        string accountId,
        string songId,
        int newScore)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM score_history
            WHERE account_id = @accountId
              AND song_id = @songId
              AND instrument = @instrument
              AND new_score = @newScore
              AND score_achieved_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", Instrument);
        cmd.Parameters.AddWithValue("newScore", newScore);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private int CountHistoryAccount(string accountId)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM score_history
            WHERE account_id = @accountId;
            """;
        cmd.Parameters.AddWithValue("accountId", accountId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private int CountDuplicateNaturalKeys()
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM (
                SELECT 1
                FROM score_history
                GROUP BY
                    account_id,
                    song_id,
                    instrument,
                    new_score,
                    score_achieved_at
                HAVING COUNT(*) > 1
            ) duplicates;
            """;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private bool IndexNullsNotDistinct()
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT index_state.indnullsnotdistinct
            FROM pg_class index_relation
            JOIN pg_namespace index_namespace
              ON index_namespace.oid = index_relation.relnamespace
            JOIN pg_index index_state
              ON index_state.indexrelid = index_relation.oid
            WHERE index_namespace.nspname = 'public'
              AND index_relation.relname = 'ix_sh_dedup';
            """;
        return Convert.ToBoolean(cmd.ExecuteScalar());
    }

    private string ReadIndexDefinition()
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT pg_get_indexdef(index_relation.oid)
            FROM pg_class index_relation
            JOIN pg_namespace index_namespace
              ON index_namespace.oid = index_relation.relnamespace
            WHERE index_namespace.nspname = 'public'
              AND index_relation.relname = 'ix_sh_dedup';
            """;
        return (string)cmd.ExecuteScalar()!;
    }

    private static int? GetNullableInt(NpgsqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static bool? GetNullableBool(
        NpgsqlDataReader reader,
        int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);

    private static DateTime Utc(int day)
        => new(2026, 1, day, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

    private sealed record HistoryRowSnapshot(
        int Id,
        string SongId,
        string Instrument,
        string AccountId,
        int? OldScore,
        int? NewScore,
        int? OldRank,
        int? NewRank,
        int? Accuracy,
        bool? IsFullCombo,
        int? Stars,
        float? Percentile,
        int? Season,
        DateTime? ScoreAchievedAt,
        int? SeasonRank,
        int? AllTimeRank,
        int? Difficulty,
        DateTime ChangedAt);
}
