using System.Globalization;
using System.Text.RegularExpressions;
using Npgsql;

namespace FSTService.Persistence;

public sealed record BandRankHistoryDailyWriteResult(
    string BandType,
    DateOnly SnapshotDate,
    bool Written,
    string Reason,
    long Rows = 0,
    long NewTeams = 0,
    int AttachAttempts = 0);

public sealed record BandRankHistoryPartitionRetentionResult(
    string BandType,
    DateOnly Cutoff,
    IReadOnlyList<string> DroppedPartitions,
    long EstimatedRowsDropped);

/// <summary>
/// Compact daily band rank history (<see cref="BandRankHistoryMode.CompactDaily"/>).
/// Once per UTC day per band type, after publication, every published band
/// ranking row is written as one compact point into a new daily partition of the
/// band type's <c>band_team_rank_history_points_v3_*</c> table, the same layout
/// the history API reads. Retention drops whole partitions.
/// </summary>
public sealed partial class MetaDatabase
{
    private const int BandRankHistoryCompactAttachAttempts = 12;

    private static readonly Regex BandRankHistoryRangeBound = new(
        @"^FOR VALUES FROM \('(?<from>\d{4}-\d{2}-\d{2})'\) TO \('(?<to>\d{4}-\d{2}-\d{2})'\)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal sealed record BandRankHistoryCompactTables(string Points, string Team, string Combo)
    {
        internal string DailyPartition(DateOnly date) => $"{Points}_d{date:yyyyMMdd}";
    }

    internal static BandRankHistoryCompactTables GetBandRankHistoryCompactTables(string bandType) => bandType switch
    {
        "Band_Duets" => new(BandRankHistoryCompactV3DuetsTable, BandRankHistoryCompactV3DuetsTeamTable, BandRankHistoryCompactV3DuetsComboTable),
        "Band_Trios" => new(BandRankHistoryCompactV3TriosTable, BandRankHistoryCompactV3TriosTeamTable, BandRankHistoryCompactV3TriosComboTable),
        "Band_Quad" => new(BandRankHistoryCompactV3QuadTable, BandRankHistoryCompactV3QuadTeamTable, BandRankHistoryCompactV3QuadComboTable),
        _ => throw new ArgumentOutOfRangeException(nameof(bandType), bandType, "Unknown band type."),
    };

    /// <summary>
    /// Writes today's compact history partition for one band type from the
    /// published rankings, unless that date's partition is already attached.
    /// </summary>
    public BandRankHistoryDailyWriteResult WriteBandRankHistoryCompactDaily(
        string bandType,
        long scrapeId,
        DateOnly snapshotDate,
        int commandTimeoutSeconds = 0,
        CancellationToken ct = default)
    {
        var tables = GetBandRankHistoryCompactTables(bandType);
        var partition = tables.DailyPartition(snapshotDate);
        using var conn = _ds.OpenConnection();
        EnsureBandRankHistoryCompactSchema(conn, bandType, tables);

        if (IsAttachedPartition(conn, partition))
            return new(bandType, snapshotDate, Written: false, Reason: "already written");

        // The published rankings are what readers see; the current tables are only
        // used where nothing has been published yet (fresh installs and tests).
        var (rankings, stats) = HasBandRankings(conn, BandRankingStorageNames.GetPublishedRankingTable(bandType), BandRankingStorageNames.GetPublishedStatsTable(bandType), bandType)
            ? (BandRankingStorageNames.GetPublishedRankingTable(bandType), BandRankingStorageNames.GetPublishedStatsTable(bandType))
            : (BandRankingStorageNames.GetCurrentRankingTable(bandType), BandRankingStorageNames.GetCurrentStatsTable(bandType));
        if (!HasBandRankings(conn, rankings, stats, bandType))
            return new(bandType, snapshotDate, Written: false, Reason: "no band rankings to record");

        ct.ThrowIfCancellationRequested();
        var newTeams = EnsureBandRankHistoryDictionaries(conn, bandType, tables, rankings, commandTimeoutSeconds, ct);
        var rows = BuildBandRankHistoryDailyPartition(
            conn, bandType, tables, partition, rankings, stats, scrapeId, snapshotDate, commandTimeoutSeconds, ct);
        var attempts = AttachBandRankHistoryDailyPartition(conn, bandType, tables, partition, snapshotDate, ct);

        try
        {
            using var tx = conn.BeginTransaction();
            ExecuteBandHistoryStatement(conn, tx, $"""
                SET LOCAL lock_timeout = '5s';
                ALTER TABLE public.{partition} DROP CONSTRAINT IF EXISTS {partition}_bound;
                ALTER TABLE public.{partition} RESET (autovacuum_enabled);
                """, 0, ct);
            tx.Commit();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            // The partition constraint already enforces the bound; the leftover CHECK
            // and autovacuum override only cost a little planning and vacuum lag.
            _log.LogInformation("Band rank history partition {Partition} tidy-up deferred: {Message}", partition, ex.Message);
        }

        return new(bandType, snapshotDate, Written: true, Reason: "written", Rows: rows, NewTeams: newTeams, AttachAttempts: attempts);
    }

    /// <summary>
    /// Drops every compact history partition of one band type whose dates all
    /// fall before <paramref name="today"/> minus <paramref name="retentionDays"/>.
    /// </summary>
    public BandRankHistoryPartitionRetentionResult DropExpiredBandRankHistoryPartitions(
        string bandType,
        DateOnly today,
        int retentionDays,
        CancellationToken ct = default)
    {
        var tables = GetBandRankHistoryCompactTables(bandType);
        var cutoff = today.AddDays(-Math.Max(1, retentionDays));
        using var conn = _ds.OpenConnection();
        if (!TableExists(conn, null, tables.Points))
            return new(bandType, cutoff, [], 0);

        var expired = ListBandRankHistoryPartitions(conn, tables.Points)
            .Where(p => p.UpperExclusive <= cutoff)
            .ToList();
        var dropped = new List<string>();
        long rows = 0;
        foreach (var partition in expired)
        {
            ct.ThrowIfCancellationRequested();
            // Dropping a partition briefly locks the parent exclusively; a short
            // lock timeout with retries keeps history reads from queueing behind it.
            RunInTransactionWithLockRetries(conn, tx => ExecuteBandHistoryStatement(conn, tx, $"""
                SET LOCAL lock_timeout = '2s';
                DROP TABLE public.{partition.Name} RESTRICT;
                """, 0, ct), ct);
            dropped.Add(partition.Name);
            rows += partition.EstimatedRows;
            _log.LogInformation(
                "Dropped expired band rank history partition {Partition} ({From}..{To}, about {Rows:N0} rows).",
                partition.Name, partition.LowerInclusive, partition.UpperExclusive, partition.EstimatedRows);
        }

        if (dropped.Count > 0)
            RefreshBandRankHistoryCompactState(conn, bandType, tables);

        return new(bandType, cutoff, dropped, rows);
    }

    internal sealed record BandRankHistoryPartition(
        string Name,
        DateOnly LowerInclusive,
        DateOnly UpperExclusive,
        long EstimatedRows);

    internal static List<BandRankHistoryPartition> ListBandRankHistoryPartitions(NpgsqlConnection conn, string pointsTable)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.relname, pg_get_expr(c.relpartbound, c.oid), GREATEST(c.reltuples, 0)::bigint
            FROM pg_inherits i
            JOIN pg_class c ON c.oid = i.inhrelid
            WHERE i.inhparent = to_regclass(@parent)
            ORDER BY c.relname
            """;
        cmd.Parameters.AddWithValue("parent", $"public.{pointsTable}");
        var partitions = new List<BandRankHistoryPartition>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var bound = BandRankHistoryRangeBound.Match(reader.GetString(1));
            if (!bound.Success)
                continue;
            partitions.Add(new(
                reader.GetString(0),
                DateOnly.ParseExact(bound.Groups["from"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateOnly.ParseExact(bound.Groups["to"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.GetInt64(2)));
        }

        return partitions;
    }

    private static bool HasBandRankings(NpgsqlConnection conn, string rankings, string stats, string bandType)
    {
        if (!TableExists(conn, null, rankings) || !TableExists(conn, null, stats))
            return false;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT EXISTS (SELECT 1 FROM {BandRankingStorageNames.QuoteIdentifier(rankings)} WHERE band_type = @bandType)";
        cmd.Parameters.AddWithValue("bandType", bandType);
        return Convert.ToBoolean(cmd.ExecuteScalar() ?? false);
    }

    private static bool IsAttachedPartition(NpgsqlConnection conn, string relation)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE((SELECT relispartition FROM pg_class WHERE oid = to_regclass(@relation)), false)";
        cmd.Parameters.AddWithValue("relation", $"public.{relation}");
        return Convert.ToBoolean(cmd.ExecuteScalar() ?? false);
    }

    /// <summary>
    /// Creates the compact history tables when missing (fresh installs and
    /// tests). Existing production tables are left untouched, so this takes no
    /// lock on them.
    /// </summary>
    private static void EnsureBandRankHistoryCompactSchema(NpgsqlConnection conn, string bandType, BandRankHistoryCompactTables tables)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            DO $$
            BEGIN
                IF to_regclass('public.{BandRankHistoryCompactV3StateTable}') IS NULL THEN
                    CREATE TABLE public.{BandRankHistoryCompactV3StateTable} (
                        band_type TEXT PRIMARY KEY,
                        status TEXT NOT NULL,
                        row_count BIGINT NOT NULL DEFAULT 0,
                        min_snapshot_date DATE,
                        max_snapshot_date DATE,
                        validated_at TIMESTAMPTZ,
                        promoted_at TIMESTAMPTZ,
                        updated_at TIMESTAMPTZ NOT NULL DEFAULT now());
                END IF;
                IF to_regclass('public.{tables.Team}') IS NULL THEN
                    CREATE TABLE public.{tables.Team} (
                        team_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                        team_key TEXT NOT NULL UNIQUE);
                END IF;
                IF to_regclass('public.{tables.Combo}') IS NULL THEN
                    CREATE TABLE public.{tables.Combo} (
                        combo_ref INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                        combo_id TEXT NOT NULL UNIQUE);
                END IF;
                IF to_regclass('public.{tables.Points}') IS NULL THEN
                    CREATE TABLE public.{tables.Points} (
                        team_id BIGINT NOT NULL,
                        scope_id SMALLINT NOT NULL CHECK (scope_id IN (0, 1)),
                        combo_ref INTEGER NOT NULL DEFAULT 0,
                        snapshot_date DATE NOT NULL,
                        snapshot_id BIGINT NOT NULL,
                        generation_id BIGINT NOT NULL,
                        snapshot_taken_at TIMESTAMPTZ NOT NULL,
                        row_fingerprint BYTEA NOT NULL CHECK (octet_length(row_fingerprint) = 16),
                        adjusted_skill_rank INT NOT NULL,
                        weighted_rank INT NOT NULL,
                        fc_rate_rank INT NOT NULL,
                        total_score_rank INT NOT NULL,
                        adjusted_skill_rating DOUBLE PRECISION,
                        weighted_rating DOUBLE PRECISION,
                        fc_rate DOUBLE PRECISION,
                        total_score BIGINT,
                        songs_played INT,
                        coverage DOUBLE PRECISION,
                        full_combo_count INT,
                        total_charted_songs INT,
                        total_ranked_teams INT,
                        raw_weighted_rating DOUBLE PRECISION,
                        raw_skill_rating DOUBLE PRECISION
                    ) PARTITION BY RANGE (snapshot_date);
                    CREATE UNIQUE INDEX {tables.Points}_uidx
                        ON public.{tables.Points} (team_id, scope_id, combo_ref, snapshot_date);
                END IF;
            END $$;
            """;
        cmd.ExecuteNonQuery();
    }

    private static long EnsureBandRankHistoryDictionaries(
        NpgsqlConnection conn,
        string bandType,
        BandRankHistoryCompactTables tables,
        string rankings,
        int commandTimeoutSeconds,
        CancellationToken ct)
    {
        using var tx = conn.BeginTransaction();
        var source = BandRankingStorageNames.QuoteIdentifier(rankings);
        ExecuteBandHistoryStatement(conn, tx, $"""
            INSERT INTO public.{tables.Combo} (combo_id)
            SELECT DISTINCT src.combo_id
            FROM {source} src
            WHERE src.band_type = @bandType
              AND src.ranking_scope <> 'overall'
              AND NOT EXISTS (SELECT 1 FROM public.{tables.Combo} c WHERE c.combo_id = src.combo_id)
            ON CONFLICT (combo_id) DO NOTHING
            """, commandTimeoutSeconds, ct, ("bandType", bandType));
        var newTeams = ExecuteBandHistoryStatement(conn, tx, $"""
            INSERT INTO public.{tables.Team} (team_key)
            SELECT DISTINCT src.team_key
            FROM {source} src
            WHERE src.band_type = @bandType
              AND NOT EXISTS (SELECT 1 FROM public.{tables.Team} t WHERE t.team_key = src.team_key)
            ON CONFLICT (team_key) DO NOTHING
            """, commandTimeoutSeconds, ct, ("bandType", bandType));
        tx.Commit();
        return newTeams;
    }

    private static long BuildBandRankHistoryDailyPartition(
        NpgsqlConnection conn,
        string bandType,
        BandRankHistoryCompactTables tables,
        string partition,
        string rankings,
        string stats,
        long scrapeId,
        DateOnly snapshotDate,
        int commandTimeoutSeconds,
        CancellationToken ct)
    {
        using var tx = conn.BeginTransaction();
        // A leftover from an interrupted build is unattached and unused.
        ExecuteBandHistoryStatement(conn, tx, $"""
            SET LOCAL lock_timeout = '5s';
            DROP TABLE IF EXISTS public.{partition} RESTRICT;
            CREATE TABLE public.{partition}
                (LIKE public.{tables.Points} INCLUDING DEFAULTS INCLUDING CONSTRAINTS)
                WITH (fillfactor = 100, autovacuum_enabled = false);
            SET LOCAL lock_timeout = 0;
            """, commandTimeoutSeconds, ct);
        var rows = ExecuteBandHistoryStatement(conn, tx, $"""
            INSERT INTO public.{partition} (
                team_id, scope_id, combo_ref, snapshot_date, snapshot_id, generation_id,
                snapshot_taken_at, row_fingerprint, adjusted_skill_rank, weighted_rank,
                fc_rate_rank, total_score_rank, adjusted_skill_rating, weighted_rating,
                fc_rate, total_score, songs_played, coverage, full_combo_count,
                total_charted_songs, total_ranked_teams, raw_weighted_rating, raw_skill_rating)
            SELECT
                team.team_id,
                CASE WHEN src.ranking_scope = 'overall' THEN 0 ELSE 1 END::smallint,
                CASE WHEN src.ranking_scope = 'overall' THEN 0 ELSE combo.combo_ref END,
                @snapshotDate,
                @scrapeId,
                src.ranking_generation,
                src.computed_at,
                decode({BandRankHistoryFingerprintExpression("src")}, 'hex'),
                src.adjusted_skill_rank,
                src.weighted_rank,
                src.fc_rate_rank,
                src.total_score_rank,
                src.adjusted_skill_rating,
                src.weighted_rating,
                src.fc_rate,
                src.total_score,
                src.songs_played,
                src.coverage,
                src.full_combo_count,
                src.total_charted_songs,
                stat.total_teams,
                src.raw_weighted_rating,
                src.raw_skill_rating
            FROM {BandRankingStorageNames.QuoteIdentifier(rankings)} src
            JOIN public.{tables.Team} team ON team.team_key = src.team_key
            LEFT JOIN public.{tables.Combo} combo
              ON src.ranking_scope <> 'overall' AND combo.combo_id = src.combo_id
            LEFT JOIN {BandRankingStorageNames.QuoteIdentifier(stats)} stat
              ON stat.band_type = src.band_type
             AND stat.ranking_scope = src.ranking_scope
             AND stat.combo_id = src.combo_id
            WHERE src.band_type = @bandType
            ORDER BY 1, 2, 3
            """, commandTimeoutSeconds, ct,
            ("bandType", bandType), ("scrapeId", scrapeId), ("snapshotDate", snapshotDate));
        var from = snapshotDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var to = snapshotDate.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        ExecuteBandHistoryStatement(conn, tx, $"""
            CREATE UNIQUE INDEX {partition}_uidx
                ON public.{partition} (team_id, scope_id, combo_ref, snapshot_date);
            ALTER TABLE public.{partition} ADD CONSTRAINT {partition}_bound
                CHECK (snapshot_date >= DATE '{from}' AND snapshot_date < DATE '{to}');
            """, commandTimeoutSeconds, ct);
        tx.Commit();

        ExecuteBandHistoryStatement(conn, null, $"ANALYZE public.{partition}", commandTimeoutSeconds, ct);
        return rows;
    }

    private static int AttachBandRankHistoryDailyPartition(
        NpgsqlConnection conn,
        string bandType,
        BandRankHistoryCompactTables tables,
        string partition,
        DateOnly snapshotDate,
        CancellationToken ct)
    {
        var from = snapshotDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var to = snapshotDate.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        // ATTACH takes SHARE UPDATE EXCLUSIVE on the parent, so history reads
        // continue; the CHECK skips the validation scan and the prebuilt unique
        // index is adopted by the parent's index. The state row moves in the same
        // transaction, so readers see the new date and its rows together.
        var attempts = RunInTransactionWithLockRetries(conn, tx =>
        {
            ExecuteBandHistoryStatement(conn, tx, $"""
                SET LOCAL lock_timeout = '2s';
                ALTER TABLE public.{tables.Points} ATTACH PARTITION public.{partition}
                    FOR VALUES FROM ('{from}') TO ('{to}');
                """, 0, ct);
            ExecuteBandHistoryStatement(conn, tx, $"""
                INSERT INTO public.{BandRankHistoryCompactV3StateTable}
                    (band_type, status, row_count, min_snapshot_date, max_snapshot_date, updated_at)
                VALUES (@bandType, 'ready', 0, @snapshotDate, @snapshotDate, now())
                ON CONFLICT (band_type) DO UPDATE SET
                    min_snapshot_date = LEAST(COALESCE({BandRankHistoryCompactV3StateTable}.min_snapshot_date, EXCLUDED.min_snapshot_date), EXCLUDED.min_snapshot_date),
                    max_snapshot_date = GREATEST(COALESCE({BandRankHistoryCompactV3StateTable}.max_snapshot_date, EXCLUDED.max_snapshot_date), EXCLUDED.max_snapshot_date),
                    updated_at = now()
                """, 0, ct, ("bandType", bandType), ("snapshotDate", snapshotDate));
        }, ct);
        RefreshBandRankHistoryCompactState(conn, bandType, tables);
        return attempts;
    }

    private static int ExecuteBandHistoryStatement(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        string sql,
        int commandTimeoutSeconds,
        CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        ConfigureCommandTimeout(cmd, commandTimeoutSeconds);
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        ct.ThrowIfCancellationRequested();
        using var registration = ct.Register(static state => ((NpgsqlCommand)state!).Cancel(), cmd);
        try
        {
            return cmd.ExecuteNonQuery();
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    private static int RunInTransactionWithLockRetries(NpgsqlConnection conn, Action<NpgsqlTransaction> body, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var tx = conn.BeginTransaction();
            try
            {
                body(tx);
                tx.Commit();
                return attempt;
            }
            catch (PostgresException ex) when (
                (ex.SqlState == PostgresErrorCodes.LockNotAvailable || ex.SqlState == PostgresErrorCodes.DeadlockDetected)
                && attempt < BandRankHistoryCompactAttachAttempts)
            {
                tx.Rollback();
                ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Min(30, 3 * attempt)));
            }
        }
    }

    /// <summary>Row count is the sum of the partitions' planner estimates.</summary>
    private static void RefreshBandRankHistoryCompactState(NpgsqlConnection conn, string bandType, BandRankHistoryCompactTables tables)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE public.{BandRankHistoryCompactV3StateTable}
            SET row_count = (
                    SELECT COALESCE(sum(GREATEST(c.reltuples, 0)), 0)::bigint
                    FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid
                    WHERE i.inhparent = to_regclass('public.{tables.Points}')),
                min_snapshot_date = (
                    SELECT min(substring(pg_get_expr(c.relpartbound, c.oid) FROM 'FROM \(''([0-9-]+)''\)')::date)
                    FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid
                    WHERE i.inhparent = to_regclass('public.{tables.Points}')),
                updated_at = now()
            WHERE band_type = @bandType
            """;
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.ExecuteNonQuery();
    }
}
