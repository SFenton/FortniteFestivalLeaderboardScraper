using Npgsql;
using NpgsqlTypes;

namespace FSTService.Persistence;

/// <summary>
/// Daily retention for solo (<c>rank_history</c>) and composite rank history.
/// A table is trimmed only when its retention cutoff has advanced since the last
/// recorded run, so most scrapes do no work. Each account keeps every row after
/// the cutoff plus its newest row at or before it.
/// </summary>
internal static class RankHistoryRetention
{
    internal const string StateTable = "rank_history_retention_state";
    internal const string CompositeScope = "composite";

    /// <summary>
    /// An uninitialized table larger than this is left to
    /// <c>tools/postgres-rank-history-retention-rewrite.py</c>, which rewrites it
    /// instead of deleting most of its rows in one statement.
    /// </summary>
    internal const long UninitializedMaxEstimatedRows = 20_000_000;

    internal static string SoloScope(string instrument) => $"solo:{instrument}";

    internal static DateOnly Cutoff(int retentionDays) =>
        DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-retentionDays);

    internal static DateOnly? ReadRetainedCutoff(NpgsqlConnection conn, NpgsqlTransaction tx, string scope)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT retained_cutoff FROM {StateTable} WHERE scope = @scope";
        cmd.Parameters.AddWithValue("scope", scope);
        return cmd.ExecuteScalar() switch
        {
            DateOnly date => date,
            DateTime dateTime => DateOnly.FromDateTime(dateTime),
            _ => null,
        };
    }

    internal static void RecordRetainedCutoff(NpgsqlConnection conn, NpgsqlTransaction tx, string scope, DateOnly cutoff)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            INSERT INTO {StateTable} (scope, retained_cutoff, updated_at)
            VALUES (@scope, @cutoff, now())
            ON CONFLICT (scope) DO UPDATE SET
                retained_cutoff = GREATEST({StateTable}.retained_cutoff, EXCLUDED.retained_cutoff),
                updated_at = now()
            """;
        cmd.Parameters.AddWithValue("scope", scope);
        cmd.Parameters.Add("cutoff", NpgsqlDbType.Date).Value = cutoff;
        cmd.ExecuteNonQuery();
    }

    internal static double EstimatedRows(NpgsqlConnection conn, NpgsqlTransaction tx, string relation)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(GREATEST(reltuples, 0), 0)::double precision FROM pg_class WHERE oid = to_regclass(@relation)";
        cmd.Parameters.AddWithValue("relation", relation);
        return cmd.ExecuteScalar() is double rows ? rows : 0;
    }

    /// <summary>
    /// Returns whether this run should delete: the cutoff advanced past the
    /// recorded one, or (first run) the table is small enough for one statement.
    /// </summary>
    internal static RetentionDecision Decide(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string scope,
        string relation,
        DateOnly cutoff,
        long uninitializedMaxEstimatedRows = UninitializedMaxEstimatedRows)
    {
        var retained = ReadRetainedCutoff(conn, tx, scope);
        if (retained is { } recorded)
            return recorded >= cutoff ? RetentionDecision.UpToDate : RetentionDecision.Delete;

        var estimatedRows = EstimatedRows(conn, tx, relation);
        return estimatedRows > uninitializedMaxEstimatedRows
            ? RetentionDecision.NeedsRewrite
            : RetentionDecision.Delete;
    }

    internal enum RetentionDecision
    {
        Delete,
        UpToDate,
        NeedsRewrite,
    }
}
