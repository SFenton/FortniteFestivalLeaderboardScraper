using System.Collections.Concurrent;
using FSTService.Scraping;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Persistence;

/// <summary>
/// Per-scope band retention floor. Band prune keeps, for each (song, band type),
/// the over-threshold rows ranked above the first valid row plus the next
/// <c>maxValidEntries</c> rows (the window), plus any team with a registered
/// member. The floor is recorded a margin of rows below the window's last row. A
/// staged row that ranks strictly below it, is not already stored, and has no
/// registered member would normally be deleted by the next prune, so the flush
/// may skip it.
/// </summary>
/// <remarks>
/// Between a prune and the next flush rows only move up (scores are maxima and
/// only prune deletes band entries). The window can still move down when rows
/// at the top become over-threshold, for example when band extraction applies
/// CHOpt validation after the flush, so the floor keeps a margin. Every skipped
/// row is recorded in the shadow table with its rank keys. The next prune
/// counts exactly how many of them it would have kept: rows that rank at or
/// above its window's last row, rows another writer stored meanwhile, and rows
/// whose team gained a registered member.
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

        CREATE TABLE IF NOT EXISTS band_retention_floor_shadow (
            song_id          TEXT NOT NULL,
            band_type        TEXT NOT NULL,
            team_key         TEXT NOT NULL,
            instrument_combo TEXT NOT NULL,
            score            INT  NOT NULL,
            end_time_key     TEXT NOT NULL,
            PRIMARY KEY (song_id, band_type, team_key, instrument_combo)
        );

        ALTER TABLE band_retention_floor_shadow ADD COLUMN IF NOT EXISTS score INT NOT NULL DEFAULT 0;
        ALTER TABLE band_retention_floor_shadow ADD COLUMN IF NOT EXISTS end_time_key TEXT NOT NULL DEFAULT '';
        -- Logged, so the evidence an Enforce flush recorded survives a crash until a
        -- prune has checked it.
        ALTER TABLE band_retention_floor_shadow SET LOGGED;

        -- Keys of the rows at smaller candidate margins below the window's last row,
        -- recorded with each floor so the next prune can report which smaller margin
        -- would also have been safe.
        -- When band prune last ran, and last ran over every scope, for the
        -- changed-scope prune.
        CREATE TABLE IF NOT EXISTS band_prune_state (
            id                 BOOLEAN     PRIMARY KEY DEFAULT TRUE CHECK (id),
            last_prune_at      TIMESTAMPTZ NOT NULL,
            last_full_prune_at TIMESTAMPTZ NOT NULL,
            max_valid_entries  INT         NOT NULL
        );

        CREATE TABLE IF NOT EXISTS band_retention_floor_margin_keys (
            song_id      TEXT NOT NULL,
            band_type    TEXT NOT NULL,
            margin_rows  INT  NOT NULL,
            score        INT  NOT NULL,
            end_time_key TEXT NOT NULL,
            PRIMARY KEY (song_id, band_type, margin_rows)
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
        SELECT s.ctid AS staging_ctid, s.song_id, s.band_type, s.team_key, s.instrument_combo,
               s.score, COALESCE(s.end_time, '') AS end_time_key
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

    private const string RecordShadowSql = """
        INSERT INTO band_retention_floor_shadow (song_id, band_type, team_key, instrument_combo, score, end_time_key)
        SELECT DISTINCT ON (b.song_id, b.band_type, b.team_key, b.instrument_combo)
               b.song_id, b.band_type, b.team_key, b.instrument_combo, b.score, b.end_time_key
        FROM below_floor b
        ORDER BY b.song_id, b.band_type, b.team_key, b.instrument_combo, b.score DESC, b.end_time_key ASC
        ON CONFLICT (song_id, band_type, team_key, instrument_combo) DO UPDATE SET
            score = GREATEST(band_retention_floor_shadow.score, EXCLUDED.score),
            end_time_key = CASE
                WHEN EXCLUDED.score > band_retention_floor_shadow.score THEN EXCLUDED.end_time_key
                WHEN EXCLUDED.score = band_retention_floor_shadow.score
                    THEN LEAST(band_retention_floor_shadow.end_time_key, EXCLUDED.end_time_key)
                ELSE band_retention_floor_shadow.end_time_key
            END
        """;

    /// <summary>Records the below-floor rows and removes them from the chunk.</summary>
    internal const string EnforceSql = $"""
        WITH below_floor AS MATERIALIZED (
        {BelowFloorSelectSql}
        ),
        recorded AS (
        {RecordShadowSql}
        )
        DELETE FROM _be_staging s
        USING below_floor b
        WHERE s.ctid = b.staging_ctid
        """;

    /// <summary>Records the below-floor rows, leaves them staged, and returns their count.</summary>
    internal const string ReportSql = $"""
        WITH below_floor AS MATERIALIZED (
        {BelowFloorSelectSql}
        ),
        recorded AS (
        {RecordShadowSql}
        )
        SELECT count(*) FROM below_floor
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

    /// <summary>
    /// Whether <see cref="PrepareForFlush"/> ran for this scrape. Writers after
    /// the flush (band extraction) use the filter only when it is active and
    /// prepared, so a skipped flush also skips the floor.
    /// </summary>
    public bool IsPrepared => Volatile.Read(ref _prepared) == 1;

    /// <summary>Turns the filter off for the rest of this scrape (for example when preparation failed).</summary>
    public void Disable() => _disabled = true;

    /// <summary>
    /// True when an earlier flush's recorded rows were still waiting for a prune,
    /// so this scrape flushes without the floor.
    /// </summary>
    public bool PendingEvidence { get; private set; }

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
    /// Ensures the schema and drops the floor of every scope whose first valid row
    /// is staged as over-threshold. Call once after the spool is complete and
    /// before the first chunk flush. When rows recorded by an earlier flush were
    /// never evaluated by a prune (for example because prune failed), the filter
    /// turns itself off for this scrape and keeps those rows for the next prune.
    /// </summary>
    public int PrepareForFlush(NpgsqlDataSource dataSource)
    {
        if (!IsActive || Interlocked.Exchange(ref _prepared, 1) == 1)
            return 0;

        BandRetentionFloorSchema.Ensure(dataSource);
        using var conn = dataSource.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var pending = conn.CreateCommand())
        {
            pending.Transaction = tx;
            pending.CommandText = "SELECT EXISTS (SELECT 1 FROM band_retention_floor_shadow)";
            if (pending.ExecuteScalar() is true)
            {
                PendingEvidence = true;
                Disable();
                tx.Commit();
                return 0;
            }
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
        var affected = Mode == BandRetentionFloorMode.Enforce
            ? cmd.ExecuteNonQuery()
            : Convert.ToInt32(cmd.ExecuteScalar());
        Interlocked.Add(ref _belowFloorRows, affected);
        return affected;
    }
}
