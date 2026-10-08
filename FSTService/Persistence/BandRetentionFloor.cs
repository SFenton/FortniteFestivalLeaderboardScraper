using System.Collections.Concurrent;
using FSTService.Scraping;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Persistence;

/// <summary>
/// Per-scope band retention floor. Band prune keeps, for each (song, band type),
/// the over-threshold rows ranked above the first valid row plus the next
/// <c>maxValidEntries</c> rows, plus any team with a registered member. The floor
/// is the last position that prune keeps unconditionally. A staged row that ranks
/// strictly below it, is not already stored, and has no registered member would be
/// deleted by the next prune, so the flush may skip it.
/// </summary>
/// <remarks>
/// Rows only move up between a prune and the next flush (scores are maxima and
/// only prune deletes band entries), so a stored floor stays conservative, with
/// one exception: when the first valid row becomes over-threshold, prune keeps
/// rows further down. Floors are therefore dropped for scopes whose first valid
/// row is staged as over-threshold, and whenever over-threshold flags are
/// recomputed outside the flush.
/// </remarks>
public static class BandRetentionFloorSchema
{
    internal const string FloorTable = "band_retention_floor";
    internal const string ShadowTable = "band_retention_floor_shadow";

    internal const string Sql = """
        CREATE TABLE IF NOT EXISTS band_retention_floor (
            song_id                      TEXT        NOT NULL,
            band_type                    TEXT        NOT NULL,
            max_valid_entries            INT         NOT NULL,
            floor_rank                   INT         NOT NULL,
            floor_score                  INT         NOT NULL,
            floor_end_time               TEXT        NOT NULL,
            first_valid_team_key         TEXT        NOT NULL,
            first_valid_instrument_combo TEXT        NOT NULL,
            computed_at                  TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (song_id, band_type)
        );

        CREATE UNLOGGED TABLE IF NOT EXISTS band_retention_floor_shadow (
            song_id          TEXT NOT NULL,
            band_type        TEXT NOT NULL,
            team_key         TEXT NOT NULL,
            instrument_combo TEXT NOT NULL,
            PRIMARY KEY (song_id, band_type, team_key, instrument_combo)
        );
        """;

    private static readonly ConcurrentDictionary<string, bool> Ensured = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates the floor tables if missing. Production applies schema only
    /// through one-shot commands, so the floor store also ensures its own small
    /// tables before first use, serialized by a transaction advisory lock.
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
            cmd.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended('fst.band_retention_floor_schema', 0))";
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

    /// <summary>Whether the floor table exists, for writers that must not create it.</summary>
    internal static bool Exists(NpgsqlConnection connection, NpgsqlTransaction? transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT to_regclass('public.band_retention_floor') IS NOT NULL";
        return cmd.ExecuteScalar() is true;
    }

    /// <summary>
    /// Drops floors for the given band types and songs (all songs when
    /// <paramref name="songIds"/> is null) after over-threshold flags were
    /// recomputed outside the flush.
    /// </summary>
    internal static int InvalidateForSongs(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyCollection<string> bandTypes,
        IReadOnlyCollection<string>? songIds)
    {
        if (!Exists(connection, transaction))
            return 0;

        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            DELETE FROM band_retention_floor
            WHERE band_type = ANY(@bandTypes)
              AND (@allSongs OR song_id = ANY(@songIds))
            """;
        cmd.Parameters.Add("bandTypes", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = bandTypes.ToArray();
        cmd.Parameters.AddWithValue("allSongs", songIds is null);
        cmd.Parameters.Add("songIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = songIds?.ToArray() ?? [];
        return cmd.ExecuteNonQuery();
    }
}

/// <summary>
/// Applies the retention floor to each band spool flush chunk. One instance
/// serves one scrape: it observes staged pages during fetch, drops unstable
/// floors once before the flush, and then filters or records each chunk.
/// </summary>
public sealed class BandRetentionFloorFilter
{
    /// <summary>
    /// Staged rows that prune would delete: strictly below the scope floor in
    /// prune order (score descending, then end time), not already stored, and
    /// with no registered member. Runs as a SELECT so the existence probe can use
    /// an index-only scan (see <see cref="BandSpoolWriterFactory.PrefilterUnchangedSql"/>).
    /// </summary>
    internal const string BelowFloorSelectSql = """
        SELECT s.ctid AS staging_ctid, s.song_id, s.band_type, s.team_key, s.instrument_combo
        FROM _be_staging s
        JOIN band_retention_floor f
          ON f.song_id = s.song_id AND f.band_type = s.band_type
        WHERE f.max_valid_entries = @maxValid
          AND (s.score < f.floor_score
               OR (s.score = f.floor_score AND COALESCE(s.end_time, '') > f.floor_end_time))
          AND NOT (s.team_members && @registeredIds)
          AND NOT EXISTS (
              SELECT 1 FROM band_entries e
              WHERE e.song_id = s.song_id AND e.band_type = s.band_type
                AND e.team_key = s.team_key AND e.instrument_combo = s.instrument_combo)
        """;

    internal const string EnforceSql = $"""
        WITH below_floor AS MATERIALIZED (
        {BelowFloorSelectSql}
        )
        DELETE FROM _be_staging s
        USING below_floor b
        WHERE s.ctid = b.staging_ctid
        """;

    internal const string ReportSql = $"""
        INSERT INTO band_retention_floor_shadow (song_id, band_type, team_key, instrument_combo)
        SELECT b.song_id, b.band_type, b.team_key, b.instrument_combo
        FROM (
        {BelowFloorSelectSql}
        ) b
        ON CONFLICT DO NOTHING
        """;

    private readonly ConcurrentDictionary<(string SongId, string BandType, string TeamKey, string InstrumentCombo), byte> _stagedOverThreshold = new();
    private readonly string[] _registeredIds;
    private long _belowFloorRows;
    private int _prepared;
    private volatile bool _disabled;

    public BandRetentionFloorFilter(
        BandRetentionFloorMode mode,
        IEnumerable<string> registeredIds,
        int maxValidEntries = BandLeaderboardPersistence.DefaultMaxValidBandEntries)
    {
        Mode = mode;
        MaxValidEntries = maxValidEntries;
        _registeredIds = registeredIds
            .Where(static id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public BandRetentionFloorMode Mode { get; }

    public int MaxValidEntries { get; }

    public bool IsActive => Mode != BandRetentionFloorMode.Off && !_disabled;

    /// <summary>Turns the filter off for the rest of this scrape (for example when preparation failed).</summary>
    public void Disable() => _disabled = true;

    /// <summary>Rows skipped (Enforce) or recorded (Report) so far.</summary>
    public long BelowFloorRows => Interlocked.Read(ref _belowFloorRows);

    /// <summary>Records staged over-threshold keys while the spool is written.</summary>
    public void Observe(string songId, string bandType, IReadOnlyList<BandLeaderboardEntry> entries)
    {
        if (!IsActive)
            return;

        foreach (var entry in entries)
        {
            if (entry.IsOverThreshold)
                _stagedOverThreshold.TryAdd((songId, bandType, entry.TeamKey, entry.InstrumentCombo), 0);
        }
    }

    /// <summary>
    /// Ensures the schema, clears any previous shadow rows, and drops the floor of
    /// every scope whose first valid row is staged as over-threshold. Call once
    /// after the spool is complete and before the first chunk flush.
    /// </summary>
    public int PrepareForFlush(NpgsqlDataSource dataSource)
    {
        if (!IsActive || Interlocked.Exchange(ref _prepared, 1) == 1)
            return 0;

        BandRetentionFloorSchema.Ensure(dataSource);
        using var conn = dataSource.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var clear = conn.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM band_retention_floor_shadow";
            clear.ExecuteNonQuery();
        }

        var invalidated = 0;
        if (!_stagedOverThreshold.IsEmpty)
        {
            var keys = _stagedOverThreshold.Keys.ToArray();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM band_retention_floor f
                USING unnest(@songIds, @bandTypes, @teamKeys, @instrumentCombos)
                    AS k(song_id, band_type, team_key, instrument_combo)
                WHERE f.song_id = k.song_id AND f.band_type = k.band_type
                  AND f.first_valid_team_key = k.team_key
                  AND f.first_valid_instrument_combo = k.instrument_combo
                """;
            cmd.Parameters.Add("songIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = keys.Select(static k => k.SongId).ToArray();
            cmd.Parameters.Add("bandTypes", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = keys.Select(static k => k.BandType).ToArray();
            cmd.Parameters.Add("teamKeys", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = keys.Select(static k => k.TeamKey).ToArray();
            cmd.Parameters.Add("instrumentCombos", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = keys.Select(static k => k.InstrumentCombo).ToArray();
            invalidated = cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return invalidated;
    }

    /// <summary>
    /// Applies the floor to the current chunk's <c>_be_staging</c> after the
    /// unchanged-row pre-filter. Enforce deletes the rows; Report records them in
    /// the shadow table and leaves them staged. Returns the affected row count.
    /// </summary>
    internal int Apply(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        if (!IsActive)
            return 0;

        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandTimeout = 0;
        cmd.CommandText = Mode == BandRetentionFloorMode.Enforce ? EnforceSql : ReportSql;
        cmd.Parameters.AddWithValue("maxValid", MaxValidEntries);
        cmd.Parameters.Add("registeredIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = _registeredIds;
        var affected = cmd.ExecuteNonQuery();
        Interlocked.Add(ref _belowFloorRows, affected);
        return affected;
    }
}
