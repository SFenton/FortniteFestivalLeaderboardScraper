using System.Collections.Concurrent;
using Npgsql;

namespace FSTService.Persistence;

/// <summary>
/// Latest rank-history row per account, kept in step with <c>rank_history</c>
/// and <c>composite_rank_history</c> so rank snapshots do not rescan the full
/// history to find each account's previous row.
/// </summary>
/// <remarks>
/// Invariant: for a scope marked ready in <c>rank_history_latest_state</c>, each
/// latest row equals the account's history row with the greatest
/// <c>snapshot_date</c>. Snapshot writes maintain it in the same transaction as
/// the history insert. Retention cleanup never deletes an account's newest row.
/// A snapshot that writes history without the latest state (option off) drops the
/// scope's readiness; the next enabled snapshot rebuilds the scope from history
/// with the original scan and then continues incrementally.
/// </remarks>
public static class RankHistoryLatestStateSchema
{
    internal const string CompositeScope = "composite";

    internal const string Sql = """
        CREATE TABLE IF NOT EXISTS rank_history_latest_state (
            scope      TEXT        PRIMARY KEY,
            ready      BOOLEAN     NOT NULL,
            built_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        -- Not partitioned: production already carries an empty table of exactly this
        -- shape from an earlier experiment, and the per-instrument reads use the
        -- (instrument, account_id) primary key.
        CREATE TABLE IF NOT EXISTS rank_history_latest (
            account_id             TEXT        NOT NULL,
            instrument             TEXT        NOT NULL,
            snapshot_date          DATE        NOT NULL,
            snapshot_taken_at      TIMESTAMPTZ,
            adjusted_skill_rank    INTEGER     NOT NULL,
            weighted_rank          INTEGER     NOT NULL,
            fc_rate_rank           INTEGER     NOT NULL,
            total_score_rank       INTEGER     NOT NULL,
            max_score_percent_rank INTEGER     NOT NULL,
            adjusted_skill_rating  REAL,
            weighted_rating        REAL,
            fc_rate                REAL,
            total_score            INTEGER,
            max_score_percent      REAL,
            songs_played           INTEGER,
            coverage               REAL,
            full_combo_count       INTEGER,
            raw_max_score_percent  REAL,
            raw_weighted_rating    REAL,
            raw_skill_rating       REAL,
            schema_version         SMALLINT    NOT NULL DEFAULT 2,
            PRIMARY KEY (instrument, account_id)
        );
        ALTER TABLE rank_history_latest SET (fillfactor = 80);

        CREATE TABLE IF NOT EXISTS composite_rank_history_latest (
            account_id         TEXT    PRIMARY KEY,
            snapshot_date      DATE    NOT NULL,
            composite_rank     INTEGER NOT NULL,
            composite_rating   REAL,
            instruments_played INTEGER,
            total_songs_played INTEGER
        );
        ALTER TABLE composite_rank_history_latest SET (fillfactor = 80);
        """;

    private static readonly ConcurrentDictionary<string, bool> Ensured = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates the latest-state tables if missing. Production applies schema
    /// only through one-shot commands, so snapshots ensure these tables before
    /// first use, serialized by a transaction advisory lock.
    /// </summary>
    public static void Ensure(NpgsqlDataSource dataSource)
    {
        if (Ensured.ContainsKey(dataSource.ConnectionString))
            return;

        using var conn = dataSource.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended('fst.rank_history_latest_schema', 0))";
            cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = Sql;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        Ensured.TryAdd(dataSource.ConnectionString, true);
    }

    /// <summary>Locks the scope's state row and reports whether it is ready.</summary>
    internal static bool LockAndReadReady(NpgsqlConnection connection, NpgsqlTransaction transaction, string scope)
    {
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO rank_history_latest_state (scope, ready)
                VALUES (@scope, FALSE)
                ON CONFLICT (scope) DO NOTHING
                """;
            insert.Parameters.AddWithValue("scope", scope);
            insert.ExecuteNonQuery();
        }

        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT ready FROM rank_history_latest_state WHERE scope = @scope FOR UPDATE";
        cmd.Parameters.AddWithValue("scope", scope);
        return cmd.ExecuteScalar() is true;
    }

    internal static void MarkReady(NpgsqlConnection connection, NpgsqlTransaction transaction, string scope, bool rebuilt)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            UPDATE rank_history_latest_state
            SET ready = TRUE,
                updated_at = now(),
                built_at = CASE WHEN @rebuilt THEN now() ELSE built_at END
            WHERE scope = @scope
            """;
        cmd.Parameters.AddWithValue("scope", scope);
        cmd.Parameters.AddWithValue("rebuilt", rebuilt);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Drops a scope's readiness after history was written without maintaining
    /// the latest state. No-op when the tables were never created.
    /// </summary>
    internal static void Invalidate(NpgsqlConnection connection, NpgsqlTransaction transaction, string scope)
    {
        using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT to_regclass('public.rank_history_latest_state') IS NOT NULL";
            if (exists.ExecuteScalar() is not true)
                return;
        }

        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "DELETE FROM rank_history_latest_state WHERE scope = @scope";
        cmd.Parameters.AddWithValue("scope", scope);
        cmd.ExecuteNonQuery();
    }
}
