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
