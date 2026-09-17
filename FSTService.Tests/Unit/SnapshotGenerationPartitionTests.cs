using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Npgsql;

namespace FSTService.Tests.Unit;

public sealed class SnapshotGenerationPartitionTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void FreshSchema_UsesInstrumentAndSnapshotGenerationPartitions()
    {
        using var connection = _fixture.DataSource.OpenConnection();

        Assert.Equal(
            "p",
            Scalar<string>(
                connection,
                """
                SELECT relkind::text
                FROM pg_class
                WHERE oid =
                    'public.leaderboard_entries_snapshot_solo_guitar'::regclass
                """));
        Assert.Equal(
            "leaderboard_entries_snapshot_solo_guitar_default",
            Scalar<string>(
                connection,
                """
                SELECT inheritance.inhrelid::regclass::text
                FROM pg_inherits inheritance
                JOIN pg_class child
                  ON child.oid = inheritance.inhrelid
                WHERE inheritance.inhparent =
                    'public.leaderboard_entries_snapshot_solo_guitar'::regclass
                  AND pg_get_expr(
                        child.relpartbound,
                        child.oid,
                        TRUE) = 'DEFAULT'
                """));
    }

    [Fact]
    public void EnsureGenerationPartition_IsIdempotentAndRoutesRows()
    {
        using var connection = _fixture.DataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();

        Assert.Equal(
            "leaderboard_entries_snapshot_solo_guitar_s2001",
            EnsureGeneration(connection, transaction, "Solo_Guitar", 2001));
        Assert.Equal(
            "leaderboard_entries_snapshot_solo_guitar_s2001",
            EnsureGeneration(connection, transaction, "Solo_Guitar", 2001));

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO leaderboard_entries_snapshot (
                    snapshot_id,
                    song_id,
                    instrument,
                    account_id,
                    score,
                    first_seen_at,
                    last_updated_at)
                VALUES (
                    @snapshotId,
                    'song-a',
                    'Solo_Guitar',
                    'account-a',
                    12345,
                    now(),
                    now())
                """;
            insert.Parameters.AddWithValue("snapshotId", 2001L);
            insert.ExecuteNonQuery();
        }

        Assert.Equal(
            "leaderboard_entries_snapshot_solo_guitar_s2001",
            Scalar<string>(
                connection,
                """
                SELECT tableoid::regclass::text
                FROM leaderboard_entries_snapshot
                WHERE snapshot_id = 2001
                  AND song_id = 'song-a'
                  AND instrument = 'Solo_Guitar'
                  AND account_id = 'account-a'
                """,
                transaction));

        transaction.Rollback();
    }

    [Fact]
    public void EnsureGenerationPartition_IsSafeBeforeDropSchemaExists()
    {
        using var connection =
            _fixture.DataSource.OpenConnection();
        using (var hideDropSchema =
               connection.CreateCommand())
        {
            hideDropSchema.CommandText = """
                ALTER TABLE snapshot_generation_drop_operations
                RENAME TO
                    snapshot_generation_drop_operations_rolling_test
                """;
            hideDropSchema.ExecuteNonQuery();
        }
        using var transaction = connection.BeginTransaction();

        Assert.Equal(
            "leaderboard_entries_snapshot_solo_guitar_s2002",
            EnsureGeneration(
                connection,
                transaction,
                "Solo_Guitar",
                2002));

        transaction.Rollback();
    }

    [Fact]
    public void EnsureGenerationPartition_IsSafeBeforeRetentionHoldSchemaExists()
    {
        using var connection =
            _fixture.DataSource.OpenConnection();
        using (var hideHoldSchema =
               connection.CreateCommand())
        {
            hideHoldSchema.CommandText = """
                ALTER TABLE snapshot_generation_retention_holds
                RENAME TO
                    snapshot_generation_retention_holds_rolling_test
                """;
            hideHoldSchema.ExecuteNonQuery();
        }
        using var transaction = connection.BeginTransaction();

        Assert.Equal(
            "leaderboard_entries_snapshot_solo_guitar_s2003",
            EnsureGeneration(
                connection,
                transaction,
                "Solo_Guitar",
                2003));

        transaction.Rollback();
    }

    [Theory]
    [InlineData("retention_in_flight")]
    [InlineData("restore_in_flight")]
    public void EnsureGenerationPartition_RejectsHeldMissingTarget(
        string holdKind)
    {
        using var connection =
            _fixture.DataSource.OpenConnection();
        using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO scrape_log (
                    id,
                    started_at,
                    completed_at,
                    status)
                VALUES (
                    2300,
                    now() - interval '10 minutes',
                    now() - interval '5 minutes',
                    'completed');
                INSERT INTO snapshot_generation_retention_holds (
                    instrument,
                    snapshot_id,
                    hold_kind,
                    reason,
                    created_by)
                VALUES (
                    'Solo_Guitar',
                    2300,
                    @holdKind,
                    'test hold',
                    'test');
                """;
            seed.Parameters.AddWithValue(
                "holdKind",
                holdKind);
            seed.ExecuteNonQuery();
        }
        using var transaction = connection.BeginTransaction();

        var failure = Assert.Throws<PostgresException>(
            () => EnsureGeneration(
                connection,
                transaction,
                "Solo_Guitar",
                2300));

        Assert.Equal("55000", failure.SqlState);
        transaction.Rollback();
        Assert.False(
            Scalar<bool>(
                connection,
                """
                SELECT to_regclass(
                    'public.leaderboard_entries_snapshot_solo_guitar_s2300')
                    IS NOT NULL
                """));
    }

    [Fact]
    public async Task EnsureGenerationPartition_RechecksHoldAfterDdlLockWait()
    {
        await using var blocker =
            await _fixture.DataSource.OpenConnectionAsync();
        await using var blockerTransaction =
            await blocker.BeginTransactionAsync();
        await using (var acquire = blocker.CreateCommand())
        {
            acquire.Transaction = blockerTransaction;
            acquire.CommandText = """
                SELECT pg_advisory_xact_lock(
                    hashtextextended(
                        'fst.snapshot-generation-partition-ddl',
                        0))
                """;
            await acquire.ExecuteNonQueryAsync();
        }

        await using var writer =
            await _fixture.DataSource.OpenConnectionAsync();
        await using var writerTransaction =
            await writer.BeginTransactionAsync();
        await using var ensure = writer.CreateCommand();
        ensure.Transaction = writerTransaction;
        ensure.CommandText = """
            SELECT ensure_leaderboard_snapshot_generation_partition(
                'Solo_Guitar',
                2301)
            """;
        var ensureTask = ensure.ExecuteScalarAsync();
        await Task.Delay(250);

        await using (var seed = blocker.CreateCommand())
        {
            seed.Transaction = blockerTransaction;
            seed.CommandText = """
                INSERT INTO scrape_log (
                    id,
                    started_at,
                    completed_at,
                    status)
                VALUES (
                    2301,
                    now() - interval '10 minutes',
                    now() - interval '5 minutes',
                    'completed');
                INSERT INTO snapshot_generation_retention_holds (
                    instrument,
                    snapshot_id,
                    hold_kind,
                    reason,
                    created_by)
                VALUES (
                    'Solo_Guitar',
                    2301,
                    'retention_in_flight',
                    'race test',
                    'test');
                """;
            await seed.ExecuteNonQueryAsync();
        }
        await blockerTransaction.CommitAsync();

        var failure =
            await Assert.ThrowsAsync<PostgresException>(
                async () => await ensureTask);
        Assert.Equal("55000", failure.SqlState);
        await writerTransaction.RollbackAsync();
        Assert.False(
            Scalar<bool>(
                writer,
                """
                SELECT to_regclass(
                    'public.leaderboard_entries_snapshot_solo_guitar_s2301')
                    IS NOT NULL
                """));
    }

    [Fact]
    public async Task EnsureGenerationPartition_SerializesConcurrentCrossInstrumentCreation()
    {
        string[] instruments =
        [
            "Solo_Guitar",
            "Solo_Bass",
            "Solo_Drums",
            "Solo_Vocals",
            "Solo_PeripheralGuitar",
            "Solo_PeripheralBass",
        ];

        for (var round = 0; round < 3; round++)
        {
            var snapshotId = 2101L + round;
            using var start = new Barrier(instruments.Length);
            var tasks = instruments.Select(instrument => Task.Run(() =>
            {
                using var connection = _fixture.DataSource.OpenConnection();
                if (!start.SignalAndWait(TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("Concurrent partition test did not synchronize.");

                LeaderboardSpoolWriterFactory.EnsureSnapshotGenerationPartition(
                    connection,
                    snapshotId,
                    instrument);
            })).ToArray();

            await Task.WhenAll(tasks);

            using var verify = _fixture.DataSource.OpenConnection();
            using var command = verify.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*)
                FROM pg_class child
                JOIN pg_namespace namespace
                  ON namespace.oid = child.relnamespace
                JOIN pg_inherits inheritance
                  ON inheritance.inhrelid = child.oid
                WHERE namespace.nspname = 'public'
                  AND child.relname = ANY(@childNames)
                  AND pg_get_expr(
                        child.relpartbound,
                        child.oid,
                        TRUE) = format(
                            'FOR VALUES IN (%L)',
                            @snapshotId)
                """;
            command.Parameters.AddWithValue(
                "childNames",
                instruments.Select(instrument =>
                    $"leaderboard_entries_snapshot_{InstrumentNameMap[instrument]}_s{snapshotId}")
                    .ToArray());
            command.Parameters.AddWithValue("snapshotId", snapshotId);
            Assert.Equal(instruments.Length, Convert.ToInt32(command.ExecuteScalar()));

            command.CommandText = """
                SELECT COUNT(*)
                FROM pg_index child_index
                JOIN pg_class child
                  ON child.oid = child_index.indrelid
                WHERE child.relname = ANY(@childNames)
                """;
            Assert.Equal(
                instruments.Length * 2,
                Convert.ToInt32(command.ExecuteScalar()));
        }
    }

    [Fact]
    public async Task EnsureGenerationPartition_WaitsBeyondCatalogLockTimeout()
    {
        using var blockerConnection = _fixture.DataSource.OpenConnection();
        using var blockerTransaction = blockerConnection.BeginTransaction();
        using (var acquire = blockerConnection.CreateCommand())
        {
            acquire.Transaction = blockerTransaction;
            acquire.CommandText =
                LeaderboardSpoolWriterFactory
                    .BuildAcquireSnapshotGenerationPartitionLockSql();
            acquire.ExecuteNonQuery();
        }

        var ensureTask = Task.Run(() =>
        {
            using var connection = _fixture.DataSource.OpenConnection();
            LeaderboardSpoolWriterFactory.EnsureSnapshotGenerationPartition(
                connection,
                2201,
                "Solo_Guitar");
        });

        await Task.Delay(TimeSpan.FromMilliseconds(2500));
        Assert.False(ensureTask.IsCompleted);

        blockerTransaction.Commit();
        await ensureTask.WaitAsync(TimeSpan.FromSeconds(10));

        using var verify = _fixture.DataSource.OpenConnection();
        Assert.Equal(
            "leaderboard_entries_snapshot_solo_guitar_s2201",
            Scalar<string>(
                verify,
                """
                SELECT inhrelid::regclass::text
                FROM pg_inherits
                WHERE inhrelid =
                    'public.leaderboard_entries_snapshot_solo_guitar_s2201'
                        ::regclass
                """));
    }

    [Fact]
    public void DroppingGenerationChild_RemovesOnlyThatSnapshot()
    {
        using var connection = _fixture.DataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();

        EnsureGeneration(connection, transaction, "Solo_Guitar", 2001);
        EnsureGeneration(connection, transaction, "Solo_Guitar", 2002);

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO leaderboard_entries_snapshot (
                    snapshot_id,
                    song_id,
                    instrument,
                    account_id,
                    score,
                    first_seen_at,
                    last_updated_at)
                VALUES
                    (2001, 'song-old', 'Solo_Guitar', 'account-old',
                     1000, now(), now()),
                    (2002, 'song-current', 'Solo_Guitar',
                     'account-current', 2000, now(), now())
                """;
            Assert.Equal(2, insert.ExecuteNonQuery());
        }

        Assert.True(
            Scalar<long>(
                connection,
                """
                SELECT pg_total_relation_size(
                    'public.leaderboard_entries_snapshot_solo_guitar_s2001')
                """,
                transaction) > 0);

        using (var drop = connection.CreateCommand())
        {
            drop.Transaction = transaction;
            drop.CommandText = """
                DROP TABLE
                    leaderboard_entries_snapshot_solo_guitar_s2001
                """;
            drop.ExecuteNonQuery();
        }

        Assert.True(
            Scalar<bool>(
                connection,
                """
                SELECT to_regclass(
                    'public.leaderboard_entries_snapshot_solo_guitar_s2001')
                    IS NULL
                """,
                transaction));
        Assert.Equal(
            0L,
            Scalar<long>(
                connection,
                """
                SELECT COUNT(*)
                FROM leaderboard_entries_snapshot
                WHERE snapshot_id = 2001
                  AND instrument = 'Solo_Guitar'
                """,
                transaction));
        Assert.Equal(
            1L,
            Scalar<long>(
                connection,
                """
                SELECT COUNT(*)
                FROM leaderboard_entries_snapshot
                WHERE snapshot_id = 2002
                  AND instrument = 'Solo_Guitar'
                """,
                transaction));
        Assert.Equal(
            0L,
            Scalar<long>(
                connection,
                """
                SELECT COUNT(*)
                FROM leaderboard_entries_snapshot_solo_guitar_default
                """,
                transaction));
        Assert.Equal(
            2L,
            Scalar<long>(
                connection,
                """
                SELECT COUNT(*)
                FROM pg_inherits inheritance
                JOIN pg_index child_index
                  ON child_index.indexrelid = inheritance.inhrelid
                WHERE inheritance.inhparent IN (
                    SELECT parent_index.indexrelid
                    FROM pg_index parent_index
                    WHERE parent_index.indrelid =
                        'public.leaderboard_entries_snapshot_solo_guitar'
                            ::regclass)
                  AND child_index.indrelid =
                        'public.leaderboard_entries_snapshot_solo_guitar_s2002'
                            ::regclass
                """,
                transaction));

        transaction.Rollback();
    }

    [Fact]
    public void EnsureGenerationPartition_RejectsUnknownInstrument()
    {
        using var connection = _fixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ensure_leaderboard_snapshot_generation_partition(
                'Solo_Unknown',
                2001)
            """;

        var error = Assert.Throws<PostgresException>(
            () => command.ExecuteScalar());

        Assert.Contains(
            "unsupported snapshot instrument",
            error.MessageText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureGenerationPartition_IsCompatibleWithLegacyRegularPartition()
    {
        using var connection = _fixture.DataSource.OpenConnection();
        using (var migrateBack = connection.CreateCommand())
        {
            migrateBack.CommandText = """
                ALTER TABLE leaderboard_entries_snapshot
                    DETACH PARTITION
                        leaderboard_entries_snapshot_solo_bass;
                DROP TABLE leaderboard_entries_snapshot_solo_bass CASCADE;
                CREATE TABLE leaderboard_entries_snapshot_solo_bass
                    PARTITION OF leaderboard_entries_snapshot
                    FOR VALUES IN ('Solo_Bass');
                """;
            migrateBack.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ensure_leaderboard_snapshot_generation_partition(
                'Solo_Bass',
                2002)
            """;

        Assert.Equal(
            "leaderboard_entries_snapshot_solo_bass",
            Assert.IsType<string>(command.ExecuteScalar()));
        Assert.Equal(
            "r",
            Scalar<string>(
                connection,
                """
                SELECT relkind::text
                FROM pg_class
                WHERE oid =
                    'public.leaderboard_entries_snapshot_solo_bass'::regclass
                """));
    }

    private static string EnsureGeneration(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string instrument,
        long snapshotId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT ensure_leaderboard_snapshot_generation_partition(
                @instrument,
                @snapshotId)
            """;
        command.Parameters.AddWithValue("instrument", instrument);
        command.Parameters.AddWithValue("snapshotId", snapshotId);
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    private static readonly IReadOnlyDictionary<string, string> InstrumentNameMap =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Solo_Guitar"] = "solo_guitar",
            ["Solo_Bass"] = "solo_bass",
            ["Solo_Drums"] = "solo_drums",
            ["Solo_Vocals"] = "solo_vocals",
            ["Solo_PeripheralGuitar"] = "pro_guitar",
            ["Solo_PeripheralBass"] = "pro_bass",
        };

    private static T Scalar<T>(
        NpgsqlConnection connection,
        string sql,
        NpgsqlTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Assert.IsType<T>(command.ExecuteScalar());
    }
}
