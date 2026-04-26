using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using FSTService.Scraping;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Persistence;

/// <summary>
/// Per-instrument leaderboard database (<see cref="IInstrumentDatabase"/> implementation).
/// All queries include WHERE instrument = @instrument since instruments share a single
/// leaderboard_entries table. MVCC handles concurrent reads/writes natively.
/// </summary>
public sealed class InstrumentDatabase : IInstrumentDatabase
{
    private readonly NpgsqlDataSource _ds;
    private readonly ILogger<InstrumentDatabase> _log;
    private readonly Lazy<bool> _rankHistoryHasPrimaryKey;
    private const string LeaderboardEntriesSnapshotTable = "leaderboard_entries_snapshot";
    private const string LeaderboardSnapshotStateTable = "leaderboard_snapshot_state";
    private const string LeaderboardEntriesOverlayTable = "leaderboard_entries_overlay";
    private const int OverlayPriorityNeighbor = 100;
    private const int OverlayPriorityPreservedCurrent = 200;
    private const string InstrumentScrapeStateTable = "instrument_scrape_state";
    private const string AccountRankingStatsTable = "account_ranking_stats";
    private const string RankHistorySnapshotStatsTable = "rank_history_snapshot_stats";
    public string Instrument { get; }

    /// <summary>When true, leeway reads resolve from interval tiers instead of dense deltas.</summary>
    public bool UseTiers { get; set; }

    /// <summary>Exposes the data source for batched writer transactions.</summary>
    internal NpgsqlDataSource DataSource => _ds;

    /// <summary>Below this entry count, use the prepared-statement loop. Above, use COPY + merge.</summary>
    internal const int BulkThreshold = 50;

    internal const int RankHistoryCleanupBatchSize = 5000;
    internal const int RankHistoryCleanupMaxBatches = 1;

    /// <summary>Serialize band member stats to compact JSON for storage. Returns null for solo entries.</summary>
    private static string? SerializeBandMembers(LeaderboardEntry e)
    {
        if (e.BandMembers is not { Count: >= 2 }) return null;
        return JsonSerializer.Serialize(e.BandMembers, BandMembersJsonContext.Default.ListBandMemberStats);
    }

    public InstrumentDatabase(string instrument, NpgsqlDataSource dataSource, ILogger<InstrumentDatabase> log)
    {
        Instrument = instrument;
        _ds = dataSource;
        _log = log;
        _rankHistoryHasPrimaryKey = new Lazy<bool>(ResolveRankHistoryHasPrimaryKey);
    }

    public void EnsureSchema() { }

    // ── Shared utility methods ───────────────────────────────────────

    private static int? GetMaxObservedSeason(IReadOnlyList<LeaderboardEntry> entries)
    {
        var maxObservedSeason = 0;
        foreach (var entry in entries)
        {
            if (entry.Season > maxObservedSeason)
                maxObservedSeason = entry.Season;
        }

        return maxObservedSeason > 0 ? maxObservedSeason : null;
    }

    private int? GetCachedMaxSeason(NpgsqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT max_observed_season FROM {InstrumentScrapeStateTable} WHERE instrument = @instrument";
        cmd.Parameters.AddWithValue("instrument", Instrument);
        var result = cmd.ExecuteScalar();
        return result is DBNull or null ? null : Convert.ToInt32(result);
    }

    private void UpsertInstrumentScrapeState(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        int maxObservedSeason)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            $"INSERT INTO {InstrumentScrapeStateTable} (instrument, max_observed_season, updated_at) " +
            "VALUES (@instrument, @maxObservedSeason, @updatedAt) " +
            $"ON CONFLICT (instrument) DO UPDATE SET " +
            $"max_observed_season = GREATEST({InstrumentScrapeStateTable}.max_observed_season, EXCLUDED.max_observed_season), " +
            $"updated_at = CASE WHEN EXCLUDED.max_observed_season >= {InstrumentScrapeStateTable}.max_observed_season THEN EXCLUDED.updated_at ELSE {InstrumentScrapeStateTable}.updated_at END";
        cmd.Parameters.AddWithValue("instrument", Instrument);
        cmd.Parameters.AddWithValue("maxObservedSeason", maxObservedSeason);
        cmd.Parameters.AddWithValue("updatedAt", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    private int? GetCachedRankedAccountCount(NpgsqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT ranked_account_count FROM {AccountRankingStatsTable} WHERE instrument = @instrument";
        cmd.Parameters.AddWithValue("instrument", Instrument);
        var result = cmd.ExecuteScalar();
        return result is DBNull or null ? null : Convert.ToInt32(result);
    }

    private int CountRankedAccounts(NpgsqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM account_rankings WHERE instrument = @instrument";
        cmd.Parameters.AddWithValue("instrument", Instrument);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private int GetRankedAccountCountWithBackfill(NpgsqlConnection conn)
    {
        var cached = GetCachedRankedAccountCount(conn);
        if (cached.HasValue)
            return cached.Value;

        var total = CountRankedAccounts(conn);
        UpsertAccountRankingStats(conn, tx: null, total, DateTime.UtcNow);
        return total;
    }

    private Dictionary<string, int> GetSongCountsFromSongStats(NpgsqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT song_id, entry_count FROM song_stats WHERE instrument = @instrument";
        cmd.Parameters.AddWithValue("instrument", Instrument);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            counts[reader.GetString(0)] = reader.GetInt32(1);
        return counts;
    }

    private Dictionary<string, int> GetSongCountsFromLeaderboardEntries(NpgsqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT song_id, COUNT(*) FROM leaderboard_entries WHERE instrument = @instrument GROUP BY song_id";
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue("instrument", Instrument);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            counts[reader.GetString(0)] = reader.GetInt32(1);
        return counts;
    }

    private void UpsertAccountRankingStats(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        int rankedAccountCount,
        DateTime computedAt)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            $"INSERT INTO {AccountRankingStatsTable} (instrument, ranked_account_count, computed_at) " +
            "VALUES (@instrument, @rankedAccountCount, @computedAt) " +
            "ON CONFLICT (instrument) DO UPDATE SET " +
            "ranked_account_count = EXCLUDED.ranked_account_count, " +
            "computed_at = EXCLUDED.computed_at";
        cmd.Parameters.AddWithValue("instrument", Instrument);
        cmd.Parameters.AddWithValue("rankedAccountCount", rankedAccountCount);
        cmd.Parameters.AddWithValue("computedAt", computedAt);
        cmd.ExecuteNonQuery();
    }

    private void UpsertRankHistorySnapshotStats(NpgsqlConnection conn, NpgsqlTransaction tx, DateOnly snapshotDate)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $@"
            INSERT INTO {RankHistorySnapshotStatsTable} (
                instrument, snapshot_date, snapshot_taken_at, total_charted_songs, ranked_account_count)
            SELECT
                @instrument,
                @snapshotDate,
                COALESCE(MAX(ars.computed_at), MAX(ar.computed_at)) AS snapshot_taken_at,
                MAX(ar.total_charted_songs) AS total_charted_songs,
                COALESCE(MAX(ars.ranked_account_count), COUNT(ar.account_id)::INTEGER) AS ranked_account_count
            FROM account_rankings ar
            LEFT JOIN {AccountRankingStatsTable} ars ON ars.instrument = ar.instrument
            WHERE ar.instrument = @instrument
            HAVING COUNT(ar.account_id) > 0
            ON CONFLICT (instrument, snapshot_date) DO UPDATE SET
                snapshot_taken_at = EXCLUDED.snapshot_taken_at,
                total_charted_songs = EXCLUDED.total_charted_songs,
                ranked_account_count = EXCLUDED.ranked_account_count";
        cmd.Parameters.AddWithValue("instrument", Instrument);
        cmd.Parameters.AddWithValue("snapshotDate", snapshotDate);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Maps a rank method name to the corresponding SQL column in <c>account_rankings</c>.
    /// Uses a whitelist to prevent SQL injection.
    /// </summary>
    internal static string MapRankColumn(string rankBy) => rankBy.ToLowerInvariant() switch
    {
        "totalscore" => "TotalScoreRank",
        "adjusted" => "AdjustedSkillRank",
        "weighted" => "WeightedRank",
        "fcrate" => "FcRateRank",
        "maxscore" => "MaxScorePercentRank",
        _ => "TotalScoreRank",
    };

    /// <summary>
    /// Extracts the rank value from an <see cref="AccountRankingDto"/> for the given metric,
    /// matching the column used by <see cref="MapRankColumn"/>.
    /// </summary>
    internal static int GetRankValue(AccountRankingDto dto, string rankBy) => rankBy.ToLowerInvariant() switch
    {
        "totalscore" => dto.TotalScoreRank,
        "adjusted" => dto.AdjustedSkillRank,
        "weighted" => dto.WeightedRank,
        "fcrate" => dto.FcRateRank,
        "maxscore" => dto.MaxScorePercentRank,
        _ => dto.TotalScoreRank,
    };

    // ── Leaderboard entries ──────────────────────────────────────────

    public int UpsertEntries(string songId, IReadOnlyList<LeaderboardEntry> entries)
    {
        if (entries.Count == 0) return 0;
        return entries.Count > BulkThreshold
            ? UpsertEntriesBulk(songId, entries)
            : UpsertEntriesLoop(songId, entries);
    }

    /// <summary>
    /// Upsert entries using an externally managed connection and transaction.
    /// Used by the pipelined writer to batch multiple songs into one PG transaction,
    /// amortizing commit overhead.
    /// </summary>
    public int UpsertEntries(string songId, IReadOnlyList<LeaderboardEntry> entries,
                             NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        if (entries.Count == 0) return 0;
        return entries.Count > BulkThreshold
            ? UpsertEntriesBulk(songId, entries, conn, tx)
            : UpsertEntriesLoop(songId, entries, conn, tx);
    }

    /// <summary>
    /// COPY binary into an unlogged temp table, then INSERT … SELECT … ON CONFLICT
    /// to merge into the real table in a single statement. 10-50x faster than the
    /// loop path for large batches.
    /// </summary>
    private int UpsertEntriesBulk(string songId, IReadOnlyList<LeaderboardEntry> entries)
    {
        var now = DateTime.UtcNow;
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();

        // Disable synchronous WAL flush for this transaction — scrape data is
        // re-scrape-able so we trade crash-safety for ~5-10x commit throughput.
        using (var sc = conn.CreateCommand()) { sc.Transaction = tx; sc.CommandText = "SET LOCAL synchronous_commit = off"; sc.ExecuteNonQuery(); }

        // 1. Create unlogged temp table (dropped at transaction end)
        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText =
                "CREATE TEMP TABLE _le_staging (" +
                "song_id TEXT, instrument TEXT, account_id TEXT, score INTEGER, accuracy INTEGER, " +
                "is_full_combo BOOLEAN, stars INTEGER, season INTEGER, difficulty INTEGER, " +
                "percentile DOUBLE PRECISION, rank INTEGER, end_time TEXT, api_rank INTEGER, " +
                "source TEXT, band_members_json JSONB, band_score INTEGER, base_score INTEGER, " +
                "instrument_bonus INTEGER, overdrive_bonus INTEGER, instrument_combo TEXT, ts TIMESTAMPTZ" +
                ") ON COMMIT DROP";
            c.ExecuteNonQuery();
        }

        // 2. COPY binary into staging
        using (var writer = conn.BeginBinaryImport(
            "COPY _le_staging (song_id, instrument, account_id, score, accuracy, is_full_combo, " +
            "stars, season, difficulty, percentile, rank, end_time, api_rank, source, " +
            "band_members_json, band_score, base_score, instrument_bonus, overdrive_bonus, instrument_combo, ts) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var e in entries)
            {
                writer.StartRow();
                writer.Write(songId, NpgsqlDbType.Text);
                writer.Write(Instrument, NpgsqlDbType.Text);
                writer.Write(e.AccountId, NpgsqlDbType.Text);
                writer.Write(e.Score, NpgsqlDbType.Integer);
                writer.Write(e.Accuracy, NpgsqlDbType.Integer);
                writer.Write(e.IsFullCombo, NpgsqlDbType.Boolean);
                writer.Write(e.Stars, NpgsqlDbType.Integer);
                writer.Write(e.Season, NpgsqlDbType.Integer);
                writer.Write(e.Difficulty, NpgsqlDbType.Integer);
                writer.Write(e.Percentile, NpgsqlDbType.Double);
                writer.Write(e.Rank, NpgsqlDbType.Integer);
                if (e.EndTime is not null) writer.Write(e.EndTime, NpgsqlDbType.Text);
                else writer.WriteNull();
                if (e.ApiRank > 0) writer.Write(e.ApiRank, NpgsqlDbType.Integer);
                else writer.WriteNull();
                writer.Write(e.Source ?? "scrape", NpgsqlDbType.Text);
                var bandJson = SerializeBandMembers(e);
                if (bandJson is not null) writer.Write(bandJson, NpgsqlDbType.Jsonb);
                else writer.WriteNull();
                if (e.BandScore.HasValue) writer.Write(e.BandScore.Value, NpgsqlDbType.Integer);
                else writer.WriteNull();
                if (e.BaseScore.HasValue) writer.Write(e.BaseScore.Value, NpgsqlDbType.Integer);
                else writer.WriteNull();
                if (e.InstrumentBonus.HasValue) writer.Write(e.InstrumentBonus.Value, NpgsqlDbType.Integer);
                else writer.WriteNull();
                if (e.OverdriveBonus.HasValue) writer.Write(e.OverdriveBonus.Value, NpgsqlDbType.Integer);
                else writer.WriteNull();
                if (e.InstrumentCombo is not null) writer.Write(e.InstrumentCombo, NpgsqlDbType.Text);
                else writer.WriteNull();
                writer.Write(now, NpgsqlDbType.TimestampTz);
            }
            writer.Complete();
        }

        // 3. Merge from staging into real table
        int affected;
        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText =
                "INSERT INTO leaderboard_entries (song_id, instrument, account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, rank, end_time, api_rank, source, band_members_json, band_score, base_score, instrument_bonus, overdrive_bonus, instrument_combo, first_seen_at, last_updated_at) " +
                "SELECT DISTINCT ON (song_id, instrument, account_id) song_id, instrument, account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, rank, end_time, api_rank, source, band_members_json, band_score, base_score, instrument_bonus, overdrive_bonus, instrument_combo, ts, ts FROM _le_staging " +
                "ORDER BY song_id, instrument, account_id, score DESC " +
                "ON CONFLICT(song_id, instrument, account_id) DO UPDATE SET " +
                "score = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.score ELSE leaderboard_entries.score END, " +
                "accuracy = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.accuracy ELSE leaderboard_entries.accuracy END, " +
                "is_full_combo = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.is_full_combo ELSE leaderboard_entries.is_full_combo END, " +
                "stars = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.stars ELSE leaderboard_entries.stars END, " +
                "season = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.season ELSE leaderboard_entries.season END, " +
                "difficulty = CASE WHEN EXCLUDED.difficulty >= 0 AND leaderboard_entries.difficulty < 0 THEN EXCLUDED.difficulty WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.difficulty ELSE leaderboard_entries.difficulty END, " +
                "percentile = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.percentile WHEN EXCLUDED.percentile > 0 AND leaderboard_entries.percentile <= 0 THEN EXCLUDED.percentile ELSE leaderboard_entries.percentile END, " +
                "rank = CASE WHEN EXCLUDED.rank > 0 THEN EXCLUDED.rank ELSE leaderboard_entries.rank END, " +
                "api_rank = CASE WHEN EXCLUDED.api_rank > 0 THEN EXCLUDED.api_rank ELSE leaderboard_entries.api_rank END, " +
                "source = CASE WHEN leaderboard_entries.source = 'scrape' THEN 'scrape' WHEN EXCLUDED.source = 'scrape' THEN 'scrape' WHEN leaderboard_entries.source = 'backfill' THEN 'backfill' WHEN EXCLUDED.source = 'backfill' THEN 'backfill' ELSE EXCLUDED.source END, " +
                "end_time = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.end_time ELSE leaderboard_entries.end_time END, " +
                "band_members_json = COALESCE(EXCLUDED.band_members_json, leaderboard_entries.band_members_json), " +
                "band_score = COALESCE(EXCLUDED.band_score, leaderboard_entries.band_score), " +
                "base_score = COALESCE(EXCLUDED.base_score, leaderboard_entries.base_score), " +
                "instrument_bonus = COALESCE(EXCLUDED.instrument_bonus, leaderboard_entries.instrument_bonus), " +
                "overdrive_bonus = COALESCE(EXCLUDED.overdrive_bonus, leaderboard_entries.overdrive_bonus), " +
                "instrument_combo = COALESCE(EXCLUDED.instrument_combo, leaderboard_entries.instrument_combo), " +
                "last_updated_at = EXCLUDED.last_updated_at";
            affected = c.ExecuteNonQuery();
        }

        var maxObservedSeason = GetMaxObservedSeason(entries);
        if (maxObservedSeason.HasValue)
            UpsertInstrumentScrapeState(conn, tx, maxObservedSeason.Value);

        SyncOverlayEntries(songId, entries, now, conn, tx);

        tx.Commit();
        return affected;
    }

    /// <summary>Bulk upsert using an externally managed connection/transaction (for batched commits).</summary>
    private int UpsertEntriesBulk(string songId, IReadOnlyList<LeaderboardEntry> entries,
                                   NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        var now = DateTime.UtcNow;

        // Drop any leftover staging table from a previous song in this batch,
        // then recreate. Cannot use ON COMMIT DROP here because the transaction
        // spans multiple songs.
        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = "DROP TABLE IF EXISTS _le_staging";
            c.ExecuteNonQuery();
        }
        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText =
                "CREATE TEMP TABLE _le_staging (" +
                "song_id TEXT, instrument TEXT, account_id TEXT, score INTEGER, accuracy INTEGER, " +
                "is_full_combo BOOLEAN, stars INTEGER, season INTEGER, difficulty INTEGER, " +
                "percentile DOUBLE PRECISION, rank INTEGER, end_time TEXT, api_rank INTEGER, " +
                "source TEXT, band_members_json JSONB, band_score INTEGER, base_score INTEGER, " +
                "instrument_bonus INTEGER, overdrive_bonus INTEGER, instrument_combo TEXT, ts TIMESTAMPTZ" +
                ")";
            c.ExecuteNonQuery();
        }

        using (var writer = conn.BeginBinaryImport(
            "COPY _le_staging (song_id, instrument, account_id, score, accuracy, is_full_combo, " +
            "stars, season, difficulty, percentile, rank, end_time, api_rank, source, " +
            "band_members_json, band_score, base_score, instrument_bonus, overdrive_bonus, instrument_combo, ts) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var e in entries)
            {
                writer.StartRow();
                writer.Write(songId, NpgsqlDbType.Text);
                writer.Write(Instrument, NpgsqlDbType.Text);
                writer.Write(e.AccountId, NpgsqlDbType.Text);
                writer.Write(e.Score, NpgsqlDbType.Integer);
                writer.Write(e.Accuracy, NpgsqlDbType.Integer);
                writer.Write(e.IsFullCombo, NpgsqlDbType.Boolean);
                writer.Write(e.Stars, NpgsqlDbType.Integer);
                writer.Write(e.Season, NpgsqlDbType.Integer);
                writer.Write(e.Difficulty, NpgsqlDbType.Integer);
                writer.Write(e.Percentile, NpgsqlDbType.Double);
                writer.Write(e.Rank, NpgsqlDbType.Integer);
                if (e.EndTime is not null) writer.Write(e.EndTime, NpgsqlDbType.Text);
                else writer.WriteNull();
                if (e.ApiRank > 0) writer.Write(e.ApiRank, NpgsqlDbType.Integer);
                else writer.WriteNull();
                writer.Write(e.Source ?? "scrape", NpgsqlDbType.Text);
                var bandJson = SerializeBandMembers(e);
                if (bandJson is not null) writer.Write(bandJson, NpgsqlDbType.Jsonb);
                else writer.WriteNull();
                if (e.BandScore.HasValue) writer.Write(e.BandScore.Value, NpgsqlDbType.Integer);
                else writer.WriteNull();
                if (e.BaseScore.HasValue) writer.Write(e.BaseScore.Value, NpgsqlDbType.Integer);
                else writer.WriteNull();
                if (e.InstrumentBonus.HasValue) writer.Write(e.InstrumentBonus.Value, NpgsqlDbType.Integer);
                else writer.WriteNull();
                if (e.OverdriveBonus.HasValue) writer.Write(e.OverdriveBonus.Value, NpgsqlDbType.Integer);
                else writer.WriteNull();
                if (e.InstrumentCombo is not null) writer.Write(e.InstrumentCombo, NpgsqlDbType.Text);
                else writer.WriteNull();
                writer.Write(now, NpgsqlDbType.TimestampTz);
            }
            writer.Complete();
        }

        int affected;
        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText =
                "INSERT INTO leaderboard_entries (song_id, instrument, account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, rank, end_time, api_rank, source, band_members_json, band_score, base_score, instrument_bonus, overdrive_bonus, instrument_combo, first_seen_at, last_updated_at) " +
                "SELECT DISTINCT ON (song_id, instrument, account_id) song_id, instrument, account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, rank, end_time, api_rank, source, band_members_json, band_score, base_score, instrument_bonus, overdrive_bonus, instrument_combo, ts, ts FROM _le_staging " +
                "ORDER BY song_id, instrument, account_id, score DESC " +
                "ON CONFLICT(song_id, instrument, account_id) DO UPDATE SET " +
                "score = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.score ELSE leaderboard_entries.score END, " +
                "accuracy = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.accuracy ELSE leaderboard_entries.accuracy END, " +
                "is_full_combo = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.is_full_combo ELSE leaderboard_entries.is_full_combo END, " +
                "stars = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.stars ELSE leaderboard_entries.stars END, " +
                "season = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.season ELSE leaderboard_entries.season END, " +
                "difficulty = CASE WHEN EXCLUDED.difficulty >= 0 AND leaderboard_entries.difficulty < 0 THEN EXCLUDED.difficulty WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.difficulty ELSE leaderboard_entries.difficulty END, " +
                "percentile = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.percentile WHEN EXCLUDED.percentile > 0 AND leaderboard_entries.percentile <= 0 THEN EXCLUDED.percentile ELSE leaderboard_entries.percentile END, " +
                "rank = CASE WHEN EXCLUDED.rank > 0 THEN EXCLUDED.rank ELSE leaderboard_entries.rank END, " +
                "api_rank = CASE WHEN EXCLUDED.api_rank > 0 THEN EXCLUDED.api_rank ELSE leaderboard_entries.api_rank END, " +
                "source = CASE WHEN leaderboard_entries.source = 'scrape' THEN 'scrape' WHEN EXCLUDED.source = 'scrape' THEN 'scrape' WHEN leaderboard_entries.source = 'backfill' THEN 'backfill' WHEN EXCLUDED.source = 'backfill' THEN 'backfill' ELSE EXCLUDED.source END, " +
                "end_time = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.end_time ELSE leaderboard_entries.end_time END, " +
                "band_members_json = COALESCE(EXCLUDED.band_members_json, leaderboard_entries.band_members_json), " +
                "band_score = COALESCE(EXCLUDED.band_score, leaderboard_entries.band_score), " +
                "base_score = COALESCE(EXCLUDED.base_score, leaderboard_entries.base_score), " +
                "instrument_bonus = COALESCE(EXCLUDED.instrument_bonus, leaderboard_entries.instrument_bonus), " +
                "overdrive_bonus = COALESCE(EXCLUDED.overdrive_bonus, leaderboard_entries.overdrive_bonus), " +
                "instrument_combo = COALESCE(EXCLUDED.instrument_combo, leaderboard_entries.instrument_combo), " +
                "last_updated_at = EXCLUDED.last_updated_at";
            affected = c.ExecuteNonQuery();
        }

        // Clean up staging table for the next song in this batch
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "DROP TABLE IF EXISTS _le_staging"; c.ExecuteNonQuery(); }

        var maxObservedSeason = GetMaxObservedSeason(entries);
        if (maxObservedSeason.HasValue)
            UpsertInstrumentScrapeState(conn, tx, maxObservedSeason.Value);

        SyncOverlayEntries(songId, entries, now, conn, tx);

        return affected;
    }

    /// <summary>Loop upsert using an externally managed connection/transaction (for batched commits).</summary>
    private int UpsertEntriesLoop(string songId, IReadOnlyList<LeaderboardEntry> entries,
                                   NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        var now = DateTime.UtcNow;
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO leaderboard_entries (song_id, instrument, account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, rank, end_time, api_rank, source, band_members_json, band_score, base_score, instrument_bonus, overdrive_bonus, instrument_combo, first_seen_at, last_updated_at) " +
            "VALUES (@songId, @instrument, @accountId, @score, @accuracy, @fc, @stars, @season, @difficulty, @pct, @rank, @endTime, @apiRank, @source, @bandJson, @bandScore, @baseScore, @instrBonus, @odBonus, @instrCombo, @now, @now) " +
            "ON CONFLICT(song_id, instrument, account_id) DO UPDATE SET " +
            "score = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.score ELSE leaderboard_entries.score END, " +
            "accuracy = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.accuracy ELSE leaderboard_entries.accuracy END, " +
            "is_full_combo = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.is_full_combo ELSE leaderboard_entries.is_full_combo END, " +
            "stars = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.stars ELSE leaderboard_entries.stars END, " +
            "season = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.season ELSE leaderboard_entries.season END, " +
            "difficulty = CASE WHEN EXCLUDED.difficulty >= 0 AND leaderboard_entries.difficulty < 0 THEN EXCLUDED.difficulty WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.difficulty ELSE leaderboard_entries.difficulty END, " +
            "percentile = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.percentile WHEN EXCLUDED.percentile > 0 AND leaderboard_entries.percentile <= 0 THEN EXCLUDED.percentile ELSE leaderboard_entries.percentile END, " +
            "rank = CASE WHEN EXCLUDED.rank > 0 THEN EXCLUDED.rank ELSE leaderboard_entries.rank END, " +
            "api_rank = CASE WHEN EXCLUDED.api_rank > 0 THEN EXCLUDED.api_rank ELSE leaderboard_entries.api_rank END, " +
            "source = CASE WHEN leaderboard_entries.source = 'scrape' THEN 'scrape' WHEN EXCLUDED.source = 'scrape' THEN 'scrape' WHEN leaderboard_entries.source = 'backfill' THEN 'backfill' WHEN EXCLUDED.source = 'backfill' THEN 'backfill' ELSE EXCLUDED.source END, " +
            "end_time = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.end_time ELSE leaderboard_entries.end_time END, " +
            "band_members_json = COALESCE(EXCLUDED.band_members_json, leaderboard_entries.band_members_json), " +
            "band_score = COALESCE(EXCLUDED.band_score, leaderboard_entries.band_score), " +
            "base_score = COALESCE(EXCLUDED.base_score, leaderboard_entries.base_score), " +
            "instrument_bonus = COALESCE(EXCLUDED.instrument_bonus, leaderboard_entries.instrument_bonus), " +
            "overdrive_bonus = COALESCE(EXCLUDED.overdrive_bonus, leaderboard_entries.overdrive_bonus), " +
            "instrument_combo = COALESCE(EXCLUDED.instrument_combo, leaderboard_entries.instrument_combo), " +
            "last_updated_at = EXCLUDED.last_updated_at " +
            "WHERE EXCLUDED.score != leaderboard_entries.score OR (EXCLUDED.rank > 0 AND leaderboard_entries.rank IS DISTINCT FROM EXCLUDED.rank) OR (EXCLUDED.api_rank > 0 AND leaderboard_entries.api_rank IS DISTINCT FROM EXCLUDED.api_rank) OR (EXCLUDED.percentile > 0 AND leaderboard_entries.percentile <= 0) OR (EXCLUDED.difficulty >= 0 AND leaderboard_entries.difficulty < 0) OR (EXCLUDED.band_members_json IS NOT NULL AND leaderboard_entries.band_members_json IS NULL)";
        var pSongId = cmd.Parameters.Add("songId", NpgsqlTypes.NpgsqlDbType.Text);
        cmd.Parameters.AddWithValue("instrument", Instrument);
        var pAccountId = cmd.Parameters.Add("accountId", NpgsqlTypes.NpgsqlDbType.Text);
        var pScore = cmd.Parameters.Add("score", NpgsqlTypes.NpgsqlDbType.Integer);
        var pAccuracy = cmd.Parameters.Add("accuracy", NpgsqlTypes.NpgsqlDbType.Integer);
        var pFc = cmd.Parameters.Add("fc", NpgsqlTypes.NpgsqlDbType.Boolean);
        var pStars = cmd.Parameters.Add("stars", NpgsqlTypes.NpgsqlDbType.Integer);
        var pSeason = cmd.Parameters.Add("season", NpgsqlTypes.NpgsqlDbType.Integer);
        var pDifficulty = cmd.Parameters.Add("difficulty", NpgsqlTypes.NpgsqlDbType.Integer);
        var pPct = cmd.Parameters.Add("pct", NpgsqlTypes.NpgsqlDbType.Double);
        var pRank = cmd.Parameters.Add("rank", NpgsqlTypes.NpgsqlDbType.Integer);
        var pEndTime = cmd.Parameters.Add("endTime", NpgsqlTypes.NpgsqlDbType.Text);
        var pApiRank = cmd.Parameters.Add("apiRank", NpgsqlTypes.NpgsqlDbType.Integer);
        var pSource = cmd.Parameters.Add("source", NpgsqlTypes.NpgsqlDbType.Text);
        var pBandJson = cmd.Parameters.Add("bandJson", NpgsqlTypes.NpgsqlDbType.Jsonb);
        var pBandScore = cmd.Parameters.Add("bandScore", NpgsqlTypes.NpgsqlDbType.Integer);
        var pBaseScore = cmd.Parameters.Add("baseScore", NpgsqlTypes.NpgsqlDbType.Integer);
        var pInstrBonus = cmd.Parameters.Add("instrBonus", NpgsqlTypes.NpgsqlDbType.Integer);
        var pOdBonus = cmd.Parameters.Add("odBonus", NpgsqlTypes.NpgsqlDbType.Integer);
        var pInstrCombo = cmd.Parameters.Add("instrCombo", NpgsqlTypes.NpgsqlDbType.Text);
        var pNow = cmd.Parameters.Add("now", NpgsqlTypes.NpgsqlDbType.TimestampTz);
        cmd.Prepare();
        int affected = 0;
        foreach (var entry in entries)
        {
            pSongId.Value = songId; pAccountId.Value = entry.AccountId; pScore.Value = entry.Score;
            pAccuracy.Value = entry.Accuracy; pFc.Value = entry.IsFullCombo;
            pStars.Value = entry.Stars; pSeason.Value = entry.Season; pDifficulty.Value = entry.Difficulty;
            pPct.Value = entry.Percentile; pRank.Value = entry.Rank;
            pEndTime.Value = (object?)entry.EndTime ?? DBNull.Value;
            pApiRank.Value = entry.ApiRank > 0 ? entry.ApiRank : DBNull.Value;
            pSource.Value = entry.Source ?? "scrape"; pNow.Value = now;
            var bandJson = SerializeBandMembers(entry);
            pBandJson.Value = (object?)bandJson ?? DBNull.Value;
            pBandScore.Value = entry.BandScore.HasValue ? entry.BandScore.Value : DBNull.Value;
            pBaseScore.Value = entry.BaseScore.HasValue ? entry.BaseScore.Value : DBNull.Value;
            pInstrBonus.Value = entry.InstrumentBonus.HasValue ? entry.InstrumentBonus.Value : DBNull.Value;
            pOdBonus.Value = entry.OverdriveBonus.HasValue ? entry.OverdriveBonus.Value : DBNull.Value;
            pInstrCombo.Value = (object?)entry.InstrumentCombo ?? DBNull.Value;
            affected += cmd.ExecuteNonQuery();
        }

        var maxObservedSeason = GetMaxObservedSeason(entries);
        if (maxObservedSeason.HasValue)
            UpsertInstrumentScrapeState(conn, tx, maxObservedSeason.Value);

        SyncOverlayEntries(songId, entries, now, conn, tx);

        return affected;
    }

    /// <summary>Prepared-statement loop for small batches (&le; <see cref="BulkThreshold"/>).</summary>
    private int UpsertEntriesLoop(string songId, IReadOnlyList<LeaderboardEntry> entries)
    {
        var now = DateTime.UtcNow;
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();

        // Disable synchronous WAL flush — same rationale as UpsertEntriesBulk.
        using (var sc = conn.CreateCommand()) { sc.Transaction = tx; sc.CommandText = "SET LOCAL synchronous_commit = off"; sc.ExecuteNonQuery(); }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO leaderboard_entries (song_id, instrument, account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, rank, end_time, api_rank, source, band_members_json, band_score, base_score, instrument_bonus, overdrive_bonus, instrument_combo, first_seen_at, last_updated_at) " +
            "VALUES (@songId, @instrument, @accountId, @score, @accuracy, @fc, @stars, @season, @difficulty, @pct, @rank, @endTime, @apiRank, @source, @bandJson, @bandScore, @baseScore, @instrBonus, @odBonus, @instrCombo, @now, @now) " +
            "ON CONFLICT(song_id, instrument, account_id) DO UPDATE SET " +
            "score = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.score ELSE leaderboard_entries.score END, " +
            "accuracy = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.accuracy ELSE leaderboard_entries.accuracy END, " +
            "is_full_combo = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.is_full_combo ELSE leaderboard_entries.is_full_combo END, " +
            "stars = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.stars ELSE leaderboard_entries.stars END, " +
            "season = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.season ELSE leaderboard_entries.season END, " +
            "difficulty = CASE WHEN EXCLUDED.difficulty >= 0 AND leaderboard_entries.difficulty < 0 THEN EXCLUDED.difficulty WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.difficulty ELSE leaderboard_entries.difficulty END, " +
            "percentile = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.percentile WHEN EXCLUDED.percentile > 0 AND leaderboard_entries.percentile <= 0 THEN EXCLUDED.percentile ELSE leaderboard_entries.percentile END, " +
            "rank = CASE WHEN EXCLUDED.rank > 0 THEN EXCLUDED.rank ELSE leaderboard_entries.rank END, " +
            "api_rank = CASE WHEN EXCLUDED.api_rank > 0 THEN EXCLUDED.api_rank ELSE leaderboard_entries.api_rank END, " +
            "source = CASE WHEN leaderboard_entries.source = 'scrape' THEN 'scrape' WHEN EXCLUDED.source = 'scrape' THEN 'scrape' WHEN leaderboard_entries.source = 'backfill' THEN 'backfill' WHEN EXCLUDED.source = 'backfill' THEN 'backfill' ELSE EXCLUDED.source END, " +
            "end_time = CASE WHEN EXCLUDED.score != leaderboard_entries.score THEN EXCLUDED.end_time ELSE leaderboard_entries.end_time END, " +
            "band_members_json = COALESCE(EXCLUDED.band_members_json, leaderboard_entries.band_members_json), " +
            "band_score = COALESCE(EXCLUDED.band_score, leaderboard_entries.band_score), " +
            "base_score = COALESCE(EXCLUDED.base_score, leaderboard_entries.base_score), " +
            "instrument_bonus = COALESCE(EXCLUDED.instrument_bonus, leaderboard_entries.instrument_bonus), " +
            "overdrive_bonus = COALESCE(EXCLUDED.overdrive_bonus, leaderboard_entries.overdrive_bonus), " +
            "instrument_combo = COALESCE(EXCLUDED.instrument_combo, leaderboard_entries.instrument_combo), " +
            "last_updated_at = EXCLUDED.last_updated_at " +
            "WHERE EXCLUDED.score != leaderboard_entries.score OR (EXCLUDED.rank > 0 AND leaderboard_entries.rank IS DISTINCT FROM EXCLUDED.rank) OR (EXCLUDED.api_rank > 0 AND leaderboard_entries.api_rank IS DISTINCT FROM EXCLUDED.api_rank) OR (EXCLUDED.percentile > 0 AND leaderboard_entries.percentile <= 0) OR (EXCLUDED.difficulty >= 0 AND leaderboard_entries.difficulty < 0) OR (EXCLUDED.band_members_json IS NOT NULL AND leaderboard_entries.band_members_json IS NULL)";
        var pSongId = cmd.Parameters.Add("songId", NpgsqlTypes.NpgsqlDbType.Text);
        cmd.Parameters.AddWithValue("instrument", Instrument);
        var pAccountId = cmd.Parameters.Add("accountId", NpgsqlTypes.NpgsqlDbType.Text);
        var pScore = cmd.Parameters.Add("score", NpgsqlTypes.NpgsqlDbType.Integer);
        var pAccuracy = cmd.Parameters.Add("accuracy", NpgsqlTypes.NpgsqlDbType.Integer);
        var pFc = cmd.Parameters.Add("fc", NpgsqlTypes.NpgsqlDbType.Boolean);
        var pStars = cmd.Parameters.Add("stars", NpgsqlTypes.NpgsqlDbType.Integer);
        var pSeason = cmd.Parameters.Add("season", NpgsqlTypes.NpgsqlDbType.Integer);
        var pDifficulty = cmd.Parameters.Add("difficulty", NpgsqlTypes.NpgsqlDbType.Integer);
        var pPct = cmd.Parameters.Add("pct", NpgsqlTypes.NpgsqlDbType.Double);
        var pRank = cmd.Parameters.Add("rank", NpgsqlTypes.NpgsqlDbType.Integer);
        var pEndTime = cmd.Parameters.Add("endTime", NpgsqlTypes.NpgsqlDbType.Text);
        var pApiRank = cmd.Parameters.Add("apiRank", NpgsqlTypes.NpgsqlDbType.Integer);
        var pSource = cmd.Parameters.Add("source", NpgsqlTypes.NpgsqlDbType.Text);
        var pBandJson = cmd.Parameters.Add("bandJson", NpgsqlTypes.NpgsqlDbType.Jsonb);
        var pBandScore = cmd.Parameters.Add("bandScore", NpgsqlTypes.NpgsqlDbType.Integer);
        var pBaseScore = cmd.Parameters.Add("baseScore", NpgsqlTypes.NpgsqlDbType.Integer);
        var pInstrBonus = cmd.Parameters.Add("instrBonus", NpgsqlTypes.NpgsqlDbType.Integer);
        var pOdBonus = cmd.Parameters.Add("odBonus", NpgsqlTypes.NpgsqlDbType.Integer);
        var pInstrCombo = cmd.Parameters.Add("instrCombo", NpgsqlTypes.NpgsqlDbType.Text);
        var pNow = cmd.Parameters.Add("now", NpgsqlTypes.NpgsqlDbType.TimestampTz);
        cmd.Prepare();
        int affected = 0;
        foreach (var entry in entries)
        {
            pSongId.Value = songId; pAccountId.Value = entry.AccountId; pScore.Value = entry.Score;
            pAccuracy.Value = entry.Accuracy; pFc.Value = entry.IsFullCombo;
            pStars.Value = entry.Stars; pSeason.Value = entry.Season; pDifficulty.Value = entry.Difficulty;
            pPct.Value = entry.Percentile; pRank.Value = entry.Rank;
            pEndTime.Value = (object?)entry.EndTime ?? DBNull.Value;
            pApiRank.Value = entry.ApiRank > 0 ? entry.ApiRank : DBNull.Value;
            pSource.Value = entry.Source ?? "scrape"; pNow.Value = now;
            var bandJson = SerializeBandMembers(entry);
            pBandJson.Value = (object?)bandJson ?? DBNull.Value;
            pBandScore.Value = entry.BandScore.HasValue ? entry.BandScore.Value : DBNull.Value;
            pBaseScore.Value = entry.BaseScore.HasValue ? entry.BaseScore.Value : DBNull.Value;
            pInstrBonus.Value = entry.InstrumentBonus.HasValue ? entry.InstrumentBonus.Value : DBNull.Value;
            pOdBonus.Value = entry.OverdriveBonus.HasValue ? entry.OverdriveBonus.Value : DBNull.Value;
            pInstrCombo.Value = (object?)entry.InstrumentCombo ?? DBNull.Value;
            affected += cmd.ExecuteNonQuery();
        }

        var maxObservedSeason = GetMaxObservedSeason(entries);
        if (maxObservedSeason.HasValue)
            UpsertInstrumentScrapeState(conn, tx, maxObservedSeason.Value);

        SyncOverlayEntries(songId, entries, now, conn, tx);

        tx.Commit();
        return affected;
    }

    public LeaderboardEntry? GetEntry(string songId, string accountId)
    {
        using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT score, accuracy, is_full_combo, stars, season, difficulty, percentile, end_time, rank, api_rank, source FROM leaderboard_entries WHERE song_id = @songId AND instrument = @instrument AND account_id = @accountId";
        cmd.Parameters.AddWithValue("songId", songId); cmd.Parameters.AddWithValue("instrument", Instrument); cmd.Parameters.AddWithValue("accountId", accountId);
        using var r = cmd.ExecuteReader(); if (!r.Read()) return null;
        return new LeaderboardEntry { AccountId = accountId, Score = r.GetInt32(0), Accuracy = r.IsDBNull(1) ? 0 : r.GetInt32(1), IsFullCombo = !r.IsDBNull(2) && r.GetBoolean(2), Stars = r.IsDBNull(3) ? 0 : r.GetInt32(3), Season = r.IsDBNull(4) ? 0 : r.GetInt32(4), Difficulty = r.IsDBNull(5) ? 0 : r.GetInt32(5), Percentile = r.IsDBNull(6) ? 0 : r.GetDouble(6), EndTime = r.IsDBNull(7) ? null : r.GetString(7), Rank = r.IsDBNull(8) ? 0 : r.GetInt32(8), ApiRank = r.IsDBNull(9) ? 0 : r.GetInt32(9), Source = r.IsDBNull(10) ? "scrape" : r.GetString(10) };
    }

    public Dictionary<string, LeaderboardEntry> GetEntriesForAccounts(string songId, IReadOnlyCollection<string> accountIds)
    {
        if (accountIds.Count == 0) return new();
        using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand();
        var pNames = new string[accountIds.Count]; int i = 0;
        foreach (var id in accountIds) { pNames[i] = $"@a{i}"; cmd.Parameters.AddWithValue($"a{i}", id); i++; }
        cmd.CommandText = $"SELECT account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, end_time, rank, api_rank, source FROM leaderboard_entries WHERE song_id = @songId AND instrument = @instrument AND account_id IN ({string.Join(",", pNames)})";
        cmd.Parameters.AddWithValue("songId", songId); cmd.Parameters.AddWithValue("instrument", Instrument);
        var dict = new Dictionary<string, LeaderboardEntry>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read()) dict[r.GetString(0)] = new LeaderboardEntry { AccountId = r.GetString(0), Score = r.GetInt32(1), Accuracy = r.IsDBNull(2) ? 0 : r.GetInt32(2), IsFullCombo = !r.IsDBNull(3) && r.GetBoolean(3), Stars = r.IsDBNull(4) ? 0 : r.GetInt32(4), Season = r.IsDBNull(5) ? 0 : r.GetInt32(5), Difficulty = r.IsDBNull(6) ? 0 : r.GetInt32(6), Percentile = r.IsDBNull(7) ? 0 : r.GetDouble(7), EndTime = r.IsDBNull(8) ? null : r.GetString(8), Rank = r.IsDBNull(9) ? 0 : r.GetInt32(9), ApiRank = r.IsDBNull(10) ? 0 : r.GetInt32(10), Source = r.IsDBNull(11) ? "scrape" : r.GetString(11) };
        return dict;
    }

    /// <summary>
    /// Get all entries across all songs for the given accounts on this instrument.
    /// Keyed by (songId, accountId).
    /// </summary>
    public Dictionary<(string SongId, string AccountId), LeaderboardEntry> GetAllEntriesForAccounts(IReadOnlyCollection<string> accountIds)
    {
        if (accountIds.Count == 0) return new();
        using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand();
        var pNames = new string[accountIds.Count]; int i = 0;
        foreach (var id in accountIds) { pNames[i] = $"@a{i}"; cmd.Parameters.AddWithValue($"a{i}", id); i++; }
        cmd.CommandText = $"SELECT song_id, account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, end_time, rank, api_rank, source FROM leaderboard_entries WHERE instrument = @instrument AND account_id IN ({string.Join(",", pNames)})";
        cmd.Parameters.AddWithValue("instrument", Instrument);
        var dict = new Dictionary<(string, string), LeaderboardEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) dict[(r.GetString(0), r.GetString(1))] = new LeaderboardEntry { AccountId = r.GetString(1), Score = r.GetInt32(2), Accuracy = r.IsDBNull(3) ? 0 : r.GetInt32(3), IsFullCombo = !r.IsDBNull(4) && r.GetBoolean(4), Stars = r.IsDBNull(5) ? 0 : r.GetInt32(5), Season = r.IsDBNull(6) ? 0 : r.GetInt32(6), Difficulty = r.IsDBNull(7) ? 0 : r.GetInt32(7), Percentile = r.IsDBNull(8) ? 0 : r.GetDouble(8), EndTime = r.IsDBNull(9) ? null : r.GetString(9), Rank = r.IsDBNull(10) ? 0 : r.GetInt32(10), ApiRank = r.IsDBNull(11) ? 0 : r.GetInt32(11), Source = r.IsDBNull(12) ? "scrape" : r.GetString(12) };
        return dict;
    }

    public int? GetMinSeason(string songId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT MIN(season) FROM leaderboard_entries WHERE song_id = @songId AND instrument = @instrument AND season > 0"; cmd.Parameters.AddWithValue("songId", songId); cmd.Parameters.AddWithValue("instrument", Instrument); var r = cmd.ExecuteScalar(); return r is DBNull or null ? null : Convert.ToInt32(r); }
    public int? GetMaxSeason()
    {
        using var conn = _ds.OpenConnection();

        var cached = GetCachedMaxSeason(conn);
        if (cached.HasValue)
            return cached.Value;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(season) FROM leaderboard_entries WHERE instrument = @instrument AND season > 0";
        cmd.Parameters.AddWithValue("instrument", Instrument);
        var result = cmd.ExecuteScalar();
        if (result is DBNull or null)
            return null;

        var maxSeason = Convert.ToInt32(result);
        UpsertInstrumentScrapeState(conn, tx: null, maxSeason);
        return maxSeason;
    }
    public long GetTotalEntryCount() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM leaderboard_entries WHERE instrument = @instrument"; cmd.Parameters.AddWithValue("instrument", Instrument); return (long)cmd.ExecuteScalar()!; }
    public string? GetAnySongId() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT song_id FROM leaderboard_entries WHERE instrument = @instrument LIMIT 1"; cmd.Parameters.AddWithValue("instrument", Instrument); var r = cmd.ExecuteScalar(); return r is DBNull or null ? null : (string)r; }

    // ── Leaderboard reads ────────────────────────────────────────────

    public List<LeaderboardEntryDto> GetLeaderboard(string songId, int? top = null, int offset = 0)
    {
        using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand();
        var limit = top.HasValue ? $"LIMIT {top.Value} OFFSET {offset}" : "";
        cmd.CommandText = $"SELECT account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, end_time, ROW_NUMBER() OVER (ORDER BY score DESC, COALESCE(end_time, first_seen_at::TEXT) ASC) AS rank, 0, api_rank, source FROM leaderboard_entries WHERE song_id = @songId AND instrument = @instrument ORDER BY score DESC, COALESCE(end_time, first_seen_at::TEXT) ASC {limit}";
        cmd.Parameters.AddWithValue("songId", songId); cmd.Parameters.AddWithValue("instrument", Instrument);
        var list = new List<LeaderboardEntryDto>(); using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadEntryDto(r));
        return list;
    }

    public int GetLeaderboardCount(string songId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM leaderboard_entries WHERE song_id = @songId AND instrument = @instrument"; cmd.Parameters.AddWithValue("songId", songId); cmd.Parameters.AddWithValue("instrument", Instrument); return Convert.ToInt32(cmd.ExecuteScalar()); }
    public Dictionary<string, int> GetAllSongCounts()
    {
        using var conn = _ds.OpenConnection();
        var counts = GetSongCountsFromSongStats(conn);
        return counts.Count > 0 ? counts : GetSongCountsFromLeaderboardEntries(conn);
    }

    public Dictionary<string, int> GetCurrentStateAllSongCounts()
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = BuildCurrentStateAllSongCountsSql();
        cmd.Parameters.AddWithValue("instrument", Instrument);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            counts[reader.GetString(0)] = reader.GetInt32(1);
        return counts;
    }

    public (List<LeaderboardEntryDto> Entries, int TotalCount) GetLeaderboardWithCount(string songId, int? top = null, int offset = 0, int? maxScore = null)
    {
        using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand();
        var scoreFilter = maxScore.HasValue ? $"AND score <= {maxScore.Value}" : "";
        var limit = top.HasValue ? $"LIMIT {top.Value} OFFSET {offset}" : "";
        cmd.CommandText = $"SELECT account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, end_time, ROW_NUMBER() OVER (ORDER BY score DESC, COALESCE(end_time, first_seen_at::TEXT) ASC) AS rank, COUNT(*) OVER () AS total_count, api_rank, source FROM leaderboard_entries WHERE song_id = @songId AND instrument = @instrument {scoreFilter} ORDER BY score DESC, COALESCE(end_time, first_seen_at::TEXT) ASC {limit}";
        cmd.Parameters.AddWithValue("songId", songId); cmd.Parameters.AddWithValue("instrument", Instrument);
        var list = new List<LeaderboardEntryDto>(); int total = 0;
        using var r = cmd.ExecuteReader();
        while (r.Read()) { list.Add(ReadEntryDto(r)); if (total == 0) total = r.GetInt32(10); }
        return (list, total);
    }

    public List<LeaderboardEntryDto> GetCurrentStateLeaderboard(string songId, int? top = null, int offset = 0)
    {
        var (entries, _) = GetCurrentStateLeaderboardCore(songId, top, offset, maxScore: null, includeTotalCount: false);
        return entries;
    }

    public (List<LeaderboardEntryDto> Entries, int TotalCount) GetCurrentStateLeaderboardWithCount(string songId, int? top = null, int offset = 0, int? maxScore = null) =>
        GetCurrentStateLeaderboardCore(songId, top, offset, maxScore, includeTotalCount: true);

    public List<(string AccountId, int Rank, int Score)> GetNeighborhood(string songId, int centerRank, int rankRadius, string excludeAccountId)
    {
        using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT account_id, rank, score FROM leaderboard_entries WHERE song_id = @songId AND instrument = @instrument AND rank BETWEEN @lo AND @hi AND account_id != @exclude";
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue("songId", songId); cmd.Parameters.AddWithValue("instrument", Instrument);
        cmd.Parameters.AddWithValue("lo", Math.Max(1, centerRank - rankRadius)); cmd.Parameters.AddWithValue("hi", centerRank + rankRadius); cmd.Parameters.AddWithValue("exclude", excludeAccountId);
        var list = new List<(string, int, int)>(); using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetInt32(1), r.GetInt32(2)));
        return list;
    }

    public List<(string AccountId, int Rank, int Score)> GetCurrentStateNeighborhood(string songId, int centerRank, int rankRadius, string excludeAccountId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = BuildCurrentStateNeighborhoodSql();
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", Instrument);
        cmd.Parameters.AddWithValue("lo", Math.Max(1, centerRank - rankRadius));
        cmd.Parameters.AddWithValue("hi", centerRank + rankRadius);
        cmd.Parameters.AddWithValue("exclude", excludeAccountId);
        var list = new List<(string, int, int)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2)));
        return list;
    }

    public List<string> GetAccountsInRankRange(string songId, int minRank, int maxRank)
    {
        if (minRank > maxRank)
            (minRank, maxRank) = (maxRank, minRank);

        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT account_id FROM leaderboard_entries WHERE song_id = @songId AND instrument = @instrument AND rank BETWEEN @lo AND @hi ORDER BY rank ASC";
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", Instrument);
        cmd.Parameters.AddWithValue("lo", Math.Max(1, minRank));
        cmd.Parameters.AddWithValue("hi", Math.Max(1, maxRank));
        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(reader.GetString(0));
        return list;
    }

    // ── Player queries ───────────────────────────────────────────────

    public HashSet<string> GetSongIdsForAccount(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT song_id FROM leaderboard_entries WHERE account_id = @accountId AND instrument = @instrument"; cmd.Parameters.AddWithValue("accountId", accountId); cmd.Parameters.AddWithValue("instrument", Instrument); var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); using var r = cmd.ExecuteReader(); while (r.Read()) set.Add(r.GetString(0)); return set; }

    public HashSet<string> GetCurrentStateSongIdsForAccount(string accountId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = BuildCurrentStateSongIdsForAccountSql();
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("instrument", Instrument);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            set.Add(reader.GetString(0));
        return set;
    }

    public List<PlayerScoreDto> GetPlayerScoresForSongs(string accountId, IReadOnlyCollection<string> songIds)
    {
        if (songIds.Count == 0) return new();
        using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand();
        var pNames = new string[songIds.Count]; int i = 0;
        foreach (var sid in songIds) { pNames[i] = $"@s{i}"; cmd.Parameters.AddWithValue($"s{i}", sid); i++; }
        cmd.CommandText = $"SELECT song_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, end_time, rank, api_rank FROM leaderboard_entries WHERE account_id = @accountId AND instrument = @instrument AND song_id IN ({string.Join(",", pNames)}) ORDER BY song_id";
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue("accountId", accountId); cmd.Parameters.AddWithValue("instrument", Instrument);
        var list = new List<PlayerScoreDto>(); using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadPlayerScore(r));
        return list;
    }

    public List<PlayerScoreDto> GetCurrentStatePlayerScoresForSongs(string accountId, IReadOnlyCollection<string> songIds)
    {
        if (songIds.Count == 0)
            return new();

        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = BuildCurrentStatePlayerScoresForSongsSql(songIds.Count);
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("instrument", Instrument);
        var parameterNames = new string[songIds.Count];
        int index = 0;
        foreach (var songId in songIds)
        {
            var name = $"s{index}";
            parameterNames[index] = $"@{name}";
            cmd.Parameters.AddWithValue(name, songId);
            index++;
        }
        var list = new List<PlayerScoreDto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(ReadPlayerScore(reader));
        return list;
    }

    public List<PlayerScoreDto> GetPlayerScores(string accountId, string? songId = null)
    {
        using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand();
        var filter = songId is not null ? "AND song_id = @songId" : "";
        cmd.CommandText = $"SELECT song_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, end_time, rank, api_rank FROM leaderboard_entries WHERE account_id = @accountId AND instrument = @instrument {filter} ORDER BY song_id";
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue("accountId", accountId); cmd.Parameters.AddWithValue("instrument", Instrument);
        if (songId is not null) cmd.Parameters.AddWithValue("songId", songId);
        var list = new List<PlayerScoreDto>(); using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadPlayerScore(r));
        return list;
    }

    public List<PlayerScoreDto> GetCurrentStatePlayerScores(string accountId, string? songId = null)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = BuildCurrentStatePlayerScoresSql(songId is not null);
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("instrument", Instrument);
        if (songId is not null)
            cmd.Parameters.AddWithValue("songId", songId);
        var list = new List<PlayerScoreDto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(ReadPlayerScore(reader));
        return list;
    }

    public Dictionary<string, int> GetPlayerRankings(string accountId, string? songId = null)
    {
        using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand();
        var songFilter = songId is not null ? "AND song_id = @songId" : "";
        cmd.CommandText = $"WITH player_songs AS (SELECT song_id FROM leaderboard_entries WHERE account_id = @accountId AND instrument = @instrument {songFilter}), ranked AS (SELECT le.account_id, le.song_id, ROW_NUMBER() OVER (PARTITION BY le.song_id ORDER BY le.score DESC, COALESCE(le.end_time, le.first_seen_at::TEXT) ASC) AS rank FROM leaderboard_entries le WHERE le.instrument = @instrument AND le.song_id IN (SELECT song_id FROM player_songs)) SELECT song_id, rank FROM ranked WHERE account_id = @accountId";
        cmd.Parameters.AddWithValue("accountId", accountId); cmd.Parameters.AddWithValue("instrument", Instrument);
        if (songId is not null) cmd.Parameters.AddWithValue("songId", songId);
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader(); while (r.Read()) dict[r.GetString(0)] = (int)r.GetInt64(1);
        return dict;
    }

    public Dictionary<string, int> GetCurrentStatePlayerRankings(string accountId, string? songId = null)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        var songFilter = songId is not null ? "AND song_id = @songId" : string.Empty;
        cmd.CommandText = $"""
            WITH current_rows AS (
                {BuildCurrentStateResolvedEntriesSql()}
            ),
            player_songs AS (
                SELECT song_id
                FROM current_rows
                WHERE account_id = @accountId {songFilter}
            ),
            ranked AS (
                SELECT account_id, song_id,
                       ROW_NUMBER() OVER (PARTITION BY song_id ORDER BY score DESC, COALESCE(end_time, first_seen_at::TEXT) ASC) AS rank
                FROM current_rows
                WHERE song_id IN (SELECT song_id FROM player_songs)
            )
            SELECT song_id, rank
            FROM ranked
            WHERE account_id = @accountId
            """;
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("instrument", Instrument);
        if (songId is not null)
            cmd.Parameters.AddWithValue("songId", songId);
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            dict[reader.GetString(0)] = (int)reader.GetInt64(1);
        return dict;
    }

    public Dictionary<string, int> GetPlayerRankingsFiltered(string accountId, Dictionary<string, int> maxScores, string? songId = null)
    {
        if (maxScores.Count == 0) return GetPlayerRankings(accountId, songId);
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "CREATE TEMP TABLE _max_thresholds (song_id TEXT PRIMARY KEY, max_score INTEGER NOT NULL) ON COMMIT DROP"; c.ExecuteNonQuery(); }
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "INSERT INTO _max_thresholds VALUES (@sid, @ms)"; var ps = c.Parameters.Add("sid", NpgsqlTypes.NpgsqlDbType.Text); var pm = c.Parameters.Add("ms", NpgsqlTypes.NpgsqlDbType.Integer); c.Prepare(); foreach (var (s, m) in maxScores) { ps.Value = s; pm.Value = m; c.ExecuteNonQuery(); } }
        using var cmd = conn.CreateCommand(); cmd.Transaction = tx;
        var songFilter = songId is not null ? "AND song_id = @songId" : "";
        cmd.CommandText = $"WITH player_songs AS (SELECT song_id FROM leaderboard_entries WHERE account_id = @accountId AND instrument = @instrument {songFilter}), ranked AS (SELECT le.account_id, le.song_id, ROW_NUMBER() OVER (PARTITION BY le.song_id ORDER BY le.score DESC, COALESCE(le.end_time, le.first_seen_at::TEXT) ASC) AS rank FROM leaderboard_entries le LEFT JOIN _max_thresholds mt ON mt.song_id = le.song_id WHERE le.instrument = @instrument AND le.song_id IN (SELECT song_id FROM player_songs) AND le.score <= COALESCE(mt.max_score, le.score + 1)) SELECT song_id, rank FROM ranked WHERE account_id = @accountId";
        cmd.Parameters.AddWithValue("accountId", accountId); cmd.Parameters.AddWithValue("instrument", Instrument);
        if (songId is not null) cmd.Parameters.AddWithValue("songId", songId);
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader(); while (r.Read()) dict[r.GetString(0)] = (int)r.GetInt64(1);
        r.Close(); tx.Commit();
        return dict;
    }

    public Dictionary<string, int> GetCurrentStatePlayerRankingsFiltered(string accountId, Dictionary<string, int> maxScores, string? songId = null)
    {
        if (maxScores.Count == 0) return GetCurrentStatePlayerRankings(accountId, songId);
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = "CREATE TEMP TABLE _max_thresholds_current_state (song_id TEXT PRIMARY KEY, max_score INTEGER NOT NULL) ON COMMIT DROP";
            c.ExecuteNonQuery();
        }
        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = "INSERT INTO _max_thresholds_current_state VALUES (@sid, @ms)";
            var songParam = c.Parameters.Add("sid", NpgsqlTypes.NpgsqlDbType.Text);
            var maxParam = c.Parameters.Add("ms", NpgsqlTypes.NpgsqlDbType.Integer);
            c.Prepare();
            foreach (var (sid, maxScore) in maxScores)
            {
                songParam.Value = sid;
                maxParam.Value = maxScore;
                c.ExecuteNonQuery();
            }
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        var songFilter = songId is not null ? "AND song_id = @songId" : string.Empty;
        cmd.CommandText = $"""
            WITH current_rows AS (
                {BuildCurrentStateResolvedEntriesSql()}
            ),
            player_songs AS (
                SELECT song_id
                FROM current_rows
                WHERE account_id = @accountId {songFilter}
            ),
            ranked AS (
                SELECT current_rows.account_id, current_rows.song_id,
                       ROW_NUMBER() OVER (PARTITION BY current_rows.song_id ORDER BY current_rows.score DESC, COALESCE(current_rows.end_time, current_rows.first_seen_at::TEXT) ASC) AS rank
                FROM current_rows
                LEFT JOIN _max_thresholds_current_state mt ON mt.song_id = current_rows.song_id
                WHERE current_rows.song_id IN (SELECT song_id FROM player_songs)
                  AND current_rows.score <= COALESCE(mt.max_score, current_rows.score + 1)
            )
            SELECT song_id, rank
            FROM ranked
            WHERE account_id = @accountId
            """;
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("instrument", Instrument);
        if (songId is not null)
            cmd.Parameters.AddWithValue("songId", songId);
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            dict[reader.GetString(0)] = (int)reader.GetInt64(1);
        reader.Close();
        tx.Commit();
        return dict;
    }

    public int GetRankForScore(string songId, int score, int? maxScore = null) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); var scoreFilter = maxScore.HasValue ? $"AND score <= {maxScore.Value}" : ""; cmd.CommandText = $"SELECT COUNT(*) + 1 FROM leaderboard_entries WHERE song_id = @songId AND instrument = @instrument AND score > @score {scoreFilter}"; cmd.Parameters.AddWithValue("songId", songId); cmd.Parameters.AddWithValue("instrument", Instrument); cmd.Parameters.AddWithValue("score", score); return Convert.ToInt32(cmd.ExecuteScalar()); }
    public int GetCurrentStateRankForScore(string songId, int score, int? maxScore = null)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        var scoreFilter = maxScore.HasValue ? "AND score <= @maxScore" : string.Empty;
        cmd.CommandText = $"""
            WITH current_rows AS (
                {BuildCurrentStateResolvedEntriesSql()}
            )
            SELECT COUNT(*) + 1
            FROM current_rows
            WHERE song_id = @songId
              AND score > @score
              {scoreFilter}
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", Instrument);
        cmd.Parameters.AddWithValue("score", score);
        if (maxScore.HasValue)
            cmd.Parameters.AddWithValue("maxScore", maxScore.Value);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
    public Dictionary<string, int> GetFilteredEntryCounts(Dictionary<string, int> maxScores) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); if (maxScores.Count == 0) return GetAllSongCounts(); using var tx = conn.BeginTransaction(); using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "CREATE TEMP TABLE _max_thresholds2 (song_id TEXT PRIMARY KEY, max_score INTEGER NOT NULL) ON COMMIT DROP"; c.ExecuteNonQuery(); } using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "INSERT INTO _max_thresholds2 VALUES (@sid, @ms)"; var ps = c.Parameters.Add("sid", NpgsqlTypes.NpgsqlDbType.Text); var pm = c.Parameters.Add("ms", NpgsqlTypes.NpgsqlDbType.Integer); c.Prepare(); foreach (var (s, m) in maxScores) { ps.Value = s; pm.Value = m; c.ExecuteNonQuery(); } } cmd.Transaction = tx; cmd.CommandText = "SELECT le.song_id, COUNT(*) FROM leaderboard_entries le LEFT JOIN _max_thresholds2 mt ON mt.song_id = le.song_id WHERE le.instrument = @instrument AND le.score <= COALESCE(mt.max_score, le.score + 1) GROUP BY le.song_id"; cmd.Parameters.AddWithValue("instrument", Instrument); var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); using var r = cmd.ExecuteReader(); while (r.Read()) dict[r.GetString(0)] = r.GetInt32(1); return dict; }
    public Dictionary<string, int> GetCurrentStateFilteredEntryCounts(Dictionary<string, int> maxScores)
    {
        if (maxScores.Count == 0) return GetCurrentStateAllSongCounts();
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = "CREATE TEMP TABLE _max_thresholds_current_state_counts (song_id TEXT PRIMARY KEY, max_score INTEGER NOT NULL) ON COMMIT DROP";
            c.ExecuteNonQuery();
        }
        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = "INSERT INTO _max_thresholds_current_state_counts VALUES (@sid, @ms)";
            var songParam = c.Parameters.Add("sid", NpgsqlTypes.NpgsqlDbType.Text);
            var maxParam = c.Parameters.Add("ms", NpgsqlTypes.NpgsqlDbType.Integer);
            c.Prepare();
            foreach (var (sid, maxScore) in maxScores)
            {
                songParam.Value = sid;
                maxParam.Value = maxScore;
                c.ExecuteNonQuery();
            }
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            WITH current_rows AS (
                {BuildCurrentStateResolvedEntriesSql()}
            )
            SELECT current_rows.song_id, COUNT(*)::INT
            FROM current_rows
            LEFT JOIN _max_thresholds_current_state_counts mt ON mt.song_id = current_rows.song_id
            WHERE current_rows.score <= COALESCE(mt.max_score, current_rows.score + 1)
            GROUP BY current_rows.song_id
            """;
        cmd.Parameters.AddWithValue("instrument", Instrument);
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            dict[reader.GetString(0)] = reader.GetInt32(1);
        reader.Close();
        tx.Commit();
        return dict;
    }
    public Dictionary<string, (int Rank, int Total)> GetPlayerStoredRankings(string accountId, string? songId = null) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); var filter = songId is not null ? "AND le.song_id = @songId" : ""; cmd.CommandText = $"SELECT le.song_id, le.rank, (SELECT COUNT(*) FROM leaderboard_entries le2 WHERE le2.song_id = le.song_id AND le2.instrument = @instrument) FROM leaderboard_entries le WHERE le.account_id = @accountId AND le.instrument = @instrument {filter}"; cmd.Parameters.AddWithValue("accountId", accountId); cmd.Parameters.AddWithValue("instrument", Instrument); if (songId is not null) cmd.Parameters.AddWithValue("songId", songId); var dict = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase); using var r = cmd.ExecuteReader(); while (r.Read()) dict[r.GetString(0)] = (r.GetInt32(1), r.GetInt32(2)); return dict; }

    // ── Rank computation ─────────────────────────────────────────────

    public int RecomputeRanksForSong(string songId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE leaderboard_entries le SET rank = sub.rn FROM " +
            "(SELECT account_id, ROW_NUMBER() OVER (ORDER BY score DESC, COALESCE(end_time, first_seen_at::TEXT) ASC) AS rn " +
            "FROM leaderboard_entries WHERE song_id = @songId AND instrument = @instrument AND source = 'scrape') sub " +
            "WHERE le.song_id = @songId AND le.account_id = sub.account_id " +
            "AND le.instrument = @instrument AND le.source = 'scrape' " +
            "AND le.rank IS DISTINCT FROM sub.rn";
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", Instrument);
        return cmd.ExecuteNonQuery();
    }

    public int RecomputeAllRanks()
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var sc = conn.CreateCommand()) { sc.Transaction = tx; sc.CommandText = "SET LOCAL synchronous_commit = off"; sc.ExecuteNonQuery(); }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = 0;
        cmd.CommandText =
            "UPDATE leaderboard_entries le SET rank = sub.rn FROM " +
            "(SELECT account_id, song_id, ROW_NUMBER() OVER (PARTITION BY song_id ORDER BY score DESC, COALESCE(end_time, first_seen_at::TEXT) ASC) AS rn " +
            "FROM leaderboard_entries WHERE instrument = @instrument AND source = 'scrape') sub " +
            "WHERE le.song_id = sub.song_id AND le.account_id = sub.account_id " +
            "AND le.instrument = @instrument AND le.source = 'scrape' " +
            "AND le.rank IS DISTINCT FROM sub.rn";
        cmd.Parameters.AddWithValue("instrument", Instrument);
        int total = cmd.ExecuteNonQuery();
        tx.Commit();
        return total;
    }

    public int RecomputeRanksForSongs(IReadOnlyCollection<string> songIds)
    {
        if (songIds.Count == 0)
            return 0;

        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var sc = conn.CreateCommand()) { sc.Transaction = tx; sc.CommandText = "SET LOCAL synchronous_commit = off"; sc.ExecuteNonQuery(); }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = 0;
        cmd.CommandText =
            "UPDATE leaderboard_entries le SET rank = sub.rn FROM " +
            "(SELECT account_id, song_id, ROW_NUMBER() OVER (PARTITION BY song_id ORDER BY score DESC, COALESCE(end_time, first_seen_at::TEXT) ASC) AS rn " +
            "FROM leaderboard_entries WHERE instrument = @instrument AND source = 'scrape' AND song_id = ANY(@songIds)) sub " +
            "WHERE le.song_id = sub.song_id AND le.account_id = sub.account_id " +
            "AND le.instrument = @instrument AND le.source = 'scrape' " +
            "AND le.rank IS DISTINCT FROM sub.rn";
        cmd.Parameters.AddWithValue("instrument", Instrument);
        cmd.Parameters.Add(new NpgsqlParameter("songIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) { Value = songIds.ToArray() });
        int total = cmd.ExecuteNonQuery();
        tx.Commit();
        return total;
    }

    // ── Pruning ──────────────────────────────────────────────────────

    public int PruneExcessEntries(string songId, int maxEntries, IReadOnlySet<string> preserveAccountIds, int? overThresholdScore = null)
    {
        int threshold = overThresholdScore ?? int.MaxValue;
        using var conn = _ds.OpenConnection(); using var tx = conn.BeginTransaction();
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "CREATE TEMP TABLE _preserve (account_id TEXT PRIMARY KEY) ON COMMIT DROP"; c.ExecuteNonQuery(); }
        if (preserveAccountIds.Count > 0) { using var c = conn.CreateCommand(); c.Transaction = tx; c.CommandText = "INSERT INTO _preserve VALUES (@id) ON CONFLICT DO NOTHING"; var p = c.Parameters.Add("id", NpgsqlTypes.NpgsqlDbType.Text); c.Prepare(); foreach (var id in preserveAccountIds) { p.Value = id; c.ExecuteNonQuery(); } }
        using var cmd = conn.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM leaderboard_entries WHERE song_id = @songId AND instrument = @instrument AND account_id NOT IN (SELECT account_id FROM _preserve) AND score <= @threshold AND ctid NOT IN (SELECT ctid FROM leaderboard_entries WHERE song_id = @songId AND instrument = @instrument AND score <= @threshold ORDER BY score DESC LIMIT @maxEntries)";
        cmd.Parameters.AddWithValue("songId", songId); cmd.Parameters.AddWithValue("instrument", Instrument); cmd.Parameters.AddWithValue("threshold", threshold); cmd.Parameters.AddWithValue("maxEntries", maxEntries);
        int deleted = cmd.ExecuteNonQuery();
        tx.Commit();
        return deleted;
    }

    public int PruneAllSongs(int maxEntriesPerSong, IReadOnlySet<string> preserveAccountIds, IReadOnlyDictionary<string, int>? songThresholds = null)
    {
        using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT song_id FROM leaderboard_entries WHERE instrument = @instrument"; cmd.Parameters.AddWithValue("instrument", Instrument);
        var songIds = new List<string>(); using var r = cmd.ExecuteReader(); while (r.Read()) songIds.Add(r.GetString(0));
        int total = 0;
        foreach (var songId in songIds) { int? threshold = songThresholds is not null && songThresholds.TryGetValue(songId, out var t) ? t : null; total += PruneExcessEntries(songId, maxEntriesPerSong, preserveAccountIds, threshold); }
        return total;
    }

    // ── Threshold band queries (for precomputation) ────────────────

    public List<int> GetScoresInBand(string songId, int lowerBound, int upperBound)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT score FROM leaderboard_entries WHERE song_id = @songId AND instrument = @instrument AND score > @lo AND score <= @hi ORDER BY score ASC";
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", Instrument);
        cmd.Parameters.AddWithValue("lo", lowerBound);
        cmd.Parameters.AddWithValue("hi", upperBound);
        var list = new List<int>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetInt32(0));
        return list;
    }

    public List<int> GetCurrentStateScoresInBand(string songId, int lowerBound, int upperBound)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH current_rows AS (
                {BuildCurrentStateResolvedEntriesSql()}
            )
            SELECT score
            FROM current_rows
            WHERE song_id = @songId
              AND score > @lo
              AND score <= @hi
            ORDER BY score ASC
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", Instrument);
        cmd.Parameters.AddWithValue("lo", lowerBound);
        cmd.Parameters.AddWithValue("hi", upperBound);
        var list = new List<int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetInt32(0));
        return list;
    }

    public int GetPopulationAtOrBelow(string songId, int threshold)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM leaderboard_entries WHERE song_id = @songId AND instrument = @instrument AND score <= @threshold";
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", Instrument);
        cmd.Parameters.AddWithValue("threshold", threshold);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int GetCurrentStatePopulationAtOrBelow(string songId, int threshold)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH current_rows AS (
                {BuildCurrentStateResolvedEntriesSql()}
            )
            SELECT COUNT(*)
            FROM current_rows
            WHERE song_id = @songId
              AND score <= @threshold
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", Instrument);
        cmd.Parameters.AddWithValue("threshold", threshold);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ── Song stats ───────────────────────────────────────────────────

    public int ComputeSongStats(Dictionary<string, int?>? maxScoresByInstrument = null, Dictionary<string, long>? realPopulation = null)
    {
        using var conn = _ds.OpenConnection();
        var prevCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var c = conn.CreateCommand()) { c.CommandText = "SELECT song_id, entry_count FROM song_stats WHERE instrument = @instrument"; c.Parameters.AddWithValue("instrument", Instrument); using var r = c.ExecuteReader(); while (r.Read()) prevCounts[r.GetString(0)] = r.GetInt32(1); }
        var freshCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var c = conn.CreateCommand()) { c.CommandText = "SELECT song_id, COUNT(*) FROM leaderboard_entries WHERE instrument = @instrument GROUP BY song_id"; c.Parameters.AddWithValue("instrument", Instrument); using var r = c.ExecuteReader(); while (r.Read()) freshCounts[r.GetString(0)] = r.GetInt32(1); }
        var allSongIds = new HashSet<string>(prevCounts.Keys.Concat(freshCounts.Keys), StringComparer.OrdinalIgnoreCase);
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO song_stats (song_id, instrument, entry_count, previous_entry_count, log_weight, max_score, computed_at) VALUES (@songId, @instrument, @entryCount, @prevCount, @logWeight, @maxScore, @now) ON CONFLICT(song_id, instrument) DO UPDATE SET previous_entry_count = song_stats.entry_count, entry_count = EXCLUDED.entry_count, log_weight = EXCLUDED.log_weight, max_score = EXCLUDED.max_score, computed_at = EXCLUDED.computed_at";
        var pSong = cmd.Parameters.Add("songId", NpgsqlTypes.NpgsqlDbType.Text); cmd.Parameters.AddWithValue("instrument", Instrument); var pEntry = cmd.Parameters.Add("entryCount", NpgsqlTypes.NpgsqlDbType.Integer); var pPrev = cmd.Parameters.Add("prevCount", NpgsqlTypes.NpgsqlDbType.Integer); var pLog = cmd.Parameters.Add("logWeight", NpgsqlTypes.NpgsqlDbType.Double); var pMax = cmd.Parameters.Add("maxScore", NpgsqlTypes.NpgsqlDbType.Integer); var pNow = cmd.Parameters.Add("now", NpgsqlTypes.NpgsqlDbType.TimestampTz); cmd.Prepare();
        int count = 0; var now = DateTime.UtcNow;
        foreach (var songId in allSongIds)
        {
            freshCounts.TryGetValue(songId, out var fresh); prevCounts.TryGetValue(songId, out var prev);
            long pop = realPopulation is not null && realPopulation.TryGetValue(songId, out var rp) && rp > 0 ? rp : 0;
            int entryCount = Math.Max(Math.Max(fresh, prev), (int)pop);
            double logWeight = entryCount > 0 ? Math.Log2(entryCount) : 0.0;
            int? maxScore = maxScoresByInstrument is not null && maxScoresByInstrument.TryGetValue(songId, out var ms) ? ms : null;
            pSong.Value = songId; pEntry.Value = entryCount; pPrev.Value = prev; pLog.Value = logWeight; pMax.Value = (object?)maxScore ?? DBNull.Value; pNow.Value = now;
            cmd.ExecuteNonQuery(); count++;
        }
        tx.Commit();
        return count;
    }

    public int ComputeCurrentStateSongStats(Dictionary<string, int?>? maxScoresByInstrument = null, Dictionary<string, long>? realPopulation = null)
    {
        using var conn = _ds.OpenConnection();
        var prevCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var c = conn.CreateCommand()) { c.CommandText = "SELECT song_id, entry_count FROM song_stats WHERE instrument = @instrument"; c.Parameters.AddWithValue("instrument", Instrument); using var r = c.ExecuteReader(); while (r.Read()) prevCounts[r.GetString(0)] = r.GetInt32(1); }
        var freshCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var c = conn.CreateCommand())
        {
            c.CommandText = BuildCurrentStateAllSongCountsSql();
            c.Parameters.AddWithValue("instrument", Instrument);
            using var r = c.ExecuteReader();
            while (r.Read())
                freshCounts[r.GetString(0)] = r.GetInt32(1);
        }
        var allSongIds = new HashSet<string>(prevCounts.Keys.Concat(freshCounts.Keys), StringComparer.OrdinalIgnoreCase);
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO song_stats (song_id, instrument, entry_count, previous_entry_count, log_weight, max_score, computed_at) VALUES (@songId, @instrument, @entryCount, @prevCount, @logWeight, @maxScore, @now) ON CONFLICT(song_id, instrument) DO UPDATE SET previous_entry_count = song_stats.entry_count, entry_count = EXCLUDED.entry_count, log_weight = EXCLUDED.log_weight, max_score = EXCLUDED.max_score, computed_at = EXCLUDED.computed_at";
        var pSong = cmd.Parameters.Add("songId", NpgsqlTypes.NpgsqlDbType.Text); cmd.Parameters.AddWithValue("instrument", Instrument); var pEntry = cmd.Parameters.Add("entryCount", NpgsqlTypes.NpgsqlDbType.Integer); var pPrev = cmd.Parameters.Add("prevCount", NpgsqlTypes.NpgsqlDbType.Integer); var pLog = cmd.Parameters.Add("logWeight", NpgsqlTypes.NpgsqlDbType.Double); var pMax = cmd.Parameters.Add("maxScore", NpgsqlTypes.NpgsqlDbType.Integer); var pNow = cmd.Parameters.Add("now", NpgsqlTypes.NpgsqlDbType.TimestampTz); cmd.Prepare();
        int count = 0; var now = DateTime.UtcNow;
        foreach (var songId in allSongIds)
        {
            freshCounts.TryGetValue(songId, out var fresh); prevCounts.TryGetValue(songId, out var prev);
            long pop = realPopulation is not null && realPopulation.TryGetValue(songId, out var rp) && rp > 0 ? rp : 0;
            int entryCount = Math.Max(Math.Max(fresh, prev), (int)pop);
            double logWeight = entryCount > 0 ? Math.Log2(entryCount) : 0.0;
            int? maxScore = maxScoresByInstrument is not null && maxScoresByInstrument.TryGetValue(songId, out var ms) ? ms : null;
            pSong.Value = songId; pEntry.Value = entryCount; pPrev.Value = prev; pLog.Value = logWeight; pMax.Value = (object?)maxScore ?? DBNull.Value; pNow.Value = now;
            cmd.ExecuteNonQuery(); count++;
        }
        tx.Commit();
        return count;
    }

    public List<(string AccountId, string SongId)> GetOverThresholdEntries()
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT le.account_id, ss.song_id
            FROM {GetPartitionName("song_stats")} ss
            CROSS JOIN LATERAL (
                SELECT le.account_id
                FROM {GetPartitionName("leaderboard_entries")} le
                WHERE le.song_id = ss.song_id
                  AND le.score > CAST(ss.max_score * 1.05 AS INTEGER)
                ORDER BY le.score DESC
            ) le
            WHERE ss.max_score IS NOT NULL";

        var list = new List<(string, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    public List<(string AccountId, string SongId)> GetCurrentStateOverThresholdEntries()
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH current_rows AS (
                {BuildCurrentStateResolvedEntriesSql()}
            )
            SELECT current_rows.account_id, ss.song_id
            FROM {GetPartitionName("song_stats")} ss
            JOIN current_rows ON current_rows.song_id = ss.song_id
            WHERE ss.max_score IS NOT NULL
              AND current_rows.score > CAST(ss.max_score * 1.05 AS INTEGER)
            ORDER BY ss.song_id, current_rows.score DESC
            """;
        cmd.Parameters.AddWithValue("instrument", Instrument);

        var list = new List<(string, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    public void PopulateValidScoreOverrides(IReadOnlyList<(string SongId, string AccountId, int Score, int? Accuracy, bool? IsFullCombo, int? Stars)> overrides)
    {
        using var conn = _ds.OpenConnection(); using var tx = conn.BeginTransaction();
        using (var c = conn.CreateCommand()) { c.Transaction = tx; var pName = GetPartitionName("valid_score_overrides"); c.CommandText = $"TRUNCATE {pName}"; c.ExecuteNonQuery(); }
        if (overrides.Count > 0) { using var c = conn.CreateCommand(); c.Transaction = tx; c.CommandText = "INSERT INTO valid_score_overrides (song_id, instrument, account_id, score, accuracy, is_full_combo, stars) VALUES (@songId, @instrument, @accountId, @score, @accuracy, @fc, @stars)"; var pSong = c.Parameters.Add("songId", NpgsqlTypes.NpgsqlDbType.Text); c.Parameters.AddWithValue("instrument", Instrument); var pAcct = c.Parameters.Add("accountId", NpgsqlTypes.NpgsqlDbType.Text); var pScore = c.Parameters.Add("score", NpgsqlTypes.NpgsqlDbType.Integer); var pAcc = c.Parameters.Add("accuracy", NpgsqlTypes.NpgsqlDbType.Integer); var pFc = c.Parameters.Add("fc", NpgsqlTypes.NpgsqlDbType.Boolean); var pStars = c.Parameters.Add("stars", NpgsqlTypes.NpgsqlDbType.Integer); c.Prepare(); foreach (var o in overrides) { pSong.Value = o.SongId; pAcct.Value = o.AccountId; pScore.Value = o.Score; pAcc.Value = (object?)o.Accuracy ?? DBNull.Value; pFc.Value = (object?)o.IsFullCombo ?? DBNull.Value; pStars.Value = (object?)o.Stars ?? DBNull.Value; c.ExecuteNonQuery(); } }
        tx.Commit();
    }

    // ── Account rankings ─────────────────────────────────────────────

    public int ComputeAccountRankings(int totalChartedSongs, int credibilityThreshold = 50, double populationMedian = 0.5, double thresholdMultiplier = 1.05)
    {
        using var conn = _ds.OpenConnection(); using var tx = conn.BeginTransaction();
        // TRUNCATE the partition directly — instant, no dead tuples, no vacuum needed
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = $"TRUNCATE {GetPartitionName("account_rankings")}"; c.ExecuteNonQuery(); }
        using var cmd = conn.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandTimeout = 0;
        var computedAt = DateTime.UtcNow;
        cmd.CommandText =
            "WITH ValidEntries AS (" +
            "SELECT le.song_id, le.account_id, le.score, le.accuracy, le.is_full_combo, le.stars, COALESCE(NULLIF(le.api_rank, 0), le.rank) AS effective_rank, ss.entry_count, ss.log_weight, ss.max_score FROM leaderboard_entries le JOIN song_stats ss ON ss.song_id = le.song_id AND ss.instrument = le.instrument WHERE le.instrument = @instrument AND le.score <= COALESCE(CAST(ss.max_score * @threshold AS INTEGER), le.score + 1) AND ss.entry_count > 0 AND COALESCE(NULLIF(le.api_rank, 0), le.rank) > 0 " +
            "UNION ALL " +
            "SELECT vso.song_id, vso.account_id, vso.score, COALESCE(vso.accuracy, 0), COALESCE(vso.is_full_combo, false), COALESCE(vso.stars, 0), (SELECT COUNT(*) + 1 FROM leaderboard_entries le2 JOIN song_stats ss2 ON ss2.song_id = le2.song_id AND ss2.instrument = le2.instrument WHERE le2.song_id = vso.song_id AND le2.instrument = @instrument AND le2.score > vso.score AND le2.score <= COALESCE(CAST(ss2.max_score * @threshold AS INTEGER), le2.score + 1) AND le2.account_id != vso.account_id), ss.entry_count, ss.log_weight, ss.max_score FROM valid_score_overrides vso JOIN song_stats ss ON ss.song_id = vso.song_id AND ss.instrument = vso.instrument WHERE vso.instrument = @instrument AND ss.entry_count > 0), " +
            "Aggregated AS (" +
            "SELECT v.account_id, COUNT(*) AS songs_played, @totalCharted AS total_charted_songs, CAST(COUNT(*) AS DOUBLE PRECISION) / @totalCharted AS coverage, AVG(CAST(v.effective_rank AS DOUBLE PRECISION) / v.entry_count) AS raw_skill_rating, SUM((CAST(v.effective_rank AS DOUBLE PRECISION) / v.entry_count) * v.log_weight) / NULLIF(SUM(v.log_weight), 0) AS weighted_rating, CAST(SUM(CASE WHEN v.is_full_combo THEN 1 ELSE 0 END) AS DOUBLE PRECISION) / @totalCharted AS fc_rate, SUM(v.score) AS total_score, AVG(CASE WHEN v.max_score IS NOT NULL AND v.max_score > 0 THEN LEAST(CAST(v.score AS DOUBLE PRECISION) / v.max_score, @threshold) ELSE NULL END) AS max_score_percent, AVG(v.accuracy) AS avg_accuracy, SUM(CASE WHEN v.is_full_combo THEN 1 ELSE 0 END) AS full_combo_count, AVG(v.stars) AS avg_stars, MIN(v.effective_rank) AS best_rank, AVG(CAST(v.effective_rank AS DOUBLE PRECISION)) AS avg_rank FROM ValidEntries v GROUP BY v.account_id), " +
            "WithBayesian AS (SELECT *, (songs_played * raw_skill_rating + @m * @C) / (songs_played + @m) AS adjusted_skill_rating, (songs_played * COALESCE(weighted_rating, 1.0) + @m * @C) / (songs_played + @m) AS adjusted_weighted_rating, (songs_played * COALESCE(max_score_percent, 0.5) + @m * @C) / (songs_played + @m) AS adjusted_max_score_percent FROM Aggregated), " +
            "Ranked AS (SELECT *, ROW_NUMBER() OVER (ORDER BY adjusted_skill_rating ASC, songs_played DESC, total_score DESC, full_combo_count DESC, account_id ASC) AS adjusted_skill_rank, ROW_NUMBER() OVER (ORDER BY adjusted_weighted_rating ASC, songs_played DESC, total_score DESC, full_combo_count DESC, account_id ASC) AS weighted_rank, ROW_NUMBER() OVER (ORDER BY fc_rate DESC, total_score DESC, songs_played DESC, adjusted_skill_rating ASC, account_id ASC) AS fc_rate_rank, ROW_NUMBER() OVER (ORDER BY total_score DESC, songs_played DESC, adjusted_skill_rating ASC, account_id ASC) AS total_score_rank, ROW_NUMBER() OVER (ORDER BY adjusted_max_score_percent DESC, songs_played DESC, adjusted_skill_rating ASC, account_id ASC) AS max_score_percent_rank FROM WithBayesian) " +
            "INSERT INTO account_rankings (account_id, instrument, songs_played, total_charted_songs, coverage, raw_skill_rating, adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank, fc_rate, fc_rate_rank, total_score, total_score_rank, max_score_percent, max_score_percent_rank, avg_accuracy, full_combo_count, avg_stars, best_rank, avg_rank, raw_max_score_percent, raw_weighted_rating, computed_at) " +
            "SELECT account_id, @instrument, songs_played, total_charted_songs, coverage, raw_skill_rating, adjusted_skill_rating, adjusted_skill_rank, adjusted_weighted_rating, weighted_rank, fc_rate, fc_rate_rank, total_score, total_score_rank, adjusted_max_score_percent, max_score_percent_rank, avg_accuracy, full_combo_count, avg_stars, best_rank, avg_rank, max_score_percent, weighted_rating, @now FROM Ranked";
        cmd.Parameters.AddWithValue("instrument", Instrument); cmd.Parameters.AddWithValue("totalCharted", totalChartedSongs); cmd.Parameters.AddWithValue("m", credibilityThreshold); cmd.Parameters.AddWithValue("C", populationMedian); cmd.Parameters.AddWithValue("threshold", thresholdMultiplier); cmd.Parameters.AddWithValue("now", computedAt);
        int rows = cmd.ExecuteNonQuery();
        UpsertAccountRankingStats(conn, tx, rows, computedAt);
        tx.Commit();
        return rows;
    }

    public int SnapshotRankHistory(int retentionDays = 365, bool cleanupRetention = true)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();

        // Legacy databases created before rank_history gained its PK cannot rely on
        // ON CONFLICT for same-day reruns, so keep the delete-and-reinsert path only
        // for that schema shape. Modern tables can compare against today's latest row
        // directly and avoid rewriting the whole day on idempotent reruns.
        if (!_rankHistoryHasPrimaryKey.Value)
        {
            using var c = conn.CreateCommand();
            c.Transaction = tx;
            c.CommandText = "DELETE FROM rank_history WHERE instrument = @instrument AND snapshot_date = @today";
            c.Parameters.AddWithValue("instrument", Instrument);
            c.Parameters.AddWithValue("today", today);
            c.ExecuteNonQuery();
        }

        // Step A: Build temp table of each account's latest snapshot.
        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = @"
                CREATE TEMP TABLE _latest_ranks ON COMMIT DROP AS
                SELECT DISTINCT ON (account_id)
                    account_id, adjusted_skill_rank, weighted_rank, fc_rate_rank,
                    total_score_rank, max_score_percent_rank,
                    adjusted_skill_rating, weighted_rating, fc_rate, total_score,
                    max_score_percent, songs_played, coverage, full_combo_count,
                    raw_max_score_percent, raw_weighted_rating, raw_skill_rating
                FROM rank_history
                WHERE instrument = @instrument
                ORDER BY account_id, snapshot_date DESC";
            c.Parameters.AddWithValue("instrument", Instrument);
            c.ExecuteNonQuery();
        }

        // Step B: Insert only changed or new accounts. On modern schemas this also
        // updates today's row in place when rankings change later the same day.
        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = @"
                INSERT INTO rank_history (account_id, instrument, snapshot_date,
                    snapshot_taken_at,
                    adjusted_skill_rank, weighted_rank, fc_rate_rank, total_score_rank, max_score_percent_rank,
                    adjusted_skill_rating, weighted_rating, fc_rate, total_score, max_score_percent,
                    songs_played, coverage, full_combo_count, raw_max_score_percent,
                    raw_weighted_rating, raw_skill_rating, schema_version)
                SELECT ar.account_id, @instrument, @today,
                    ar.computed_at,
                    ar.adjusted_skill_rank, ar.weighted_rank, ar.fc_rate_rank, ar.total_score_rank, ar.max_score_percent_rank,
                    ar.adjusted_skill_rating, ar.weighted_rating, ar.fc_rate, ar.total_score, ar.max_score_percent,
                    ar.songs_played, ar.coverage, ar.full_combo_count, ar.raw_max_score_percent,
                    ar.raw_weighted_rating, ar.raw_skill_rating, 2
                FROM account_rankings ar
                LEFT JOIN _latest_ranks lr ON lr.account_id = ar.account_id
                WHERE ar.instrument = @instrument
                  AND (
                    lr.account_id IS NULL
                    OR lr.adjusted_skill_rank IS DISTINCT FROM ar.adjusted_skill_rank
                    OR lr.weighted_rank IS DISTINCT FROM ar.weighted_rank
                    OR lr.fc_rate_rank IS DISTINCT FROM ar.fc_rate_rank
                    OR lr.total_score_rank IS DISTINCT FROM ar.total_score_rank
                    OR lr.max_score_percent_rank IS DISTINCT FROM ar.max_score_percent_rank
                    OR lr.adjusted_skill_rating IS DISTINCT FROM ar.adjusted_skill_rating
                    OR lr.weighted_rating IS DISTINCT FROM ar.weighted_rating
                    OR lr.fc_rate IS DISTINCT FROM ar.fc_rate
                    OR lr.total_score IS DISTINCT FROM ar.total_score
                    OR lr.max_score_percent IS DISTINCT FROM ar.max_score_percent
                    OR lr.songs_played IS DISTINCT FROM ar.songs_played
                    OR lr.coverage IS DISTINCT FROM ar.coverage
                    OR lr.full_combo_count IS DISTINCT FROM ar.full_combo_count
                    OR lr.raw_max_score_percent IS DISTINCT FROM ar.raw_max_score_percent
                  )
                ON CONFLICT (account_id, instrument, snapshot_date) DO UPDATE SET
                                        snapshot_taken_at = EXCLUDED.snapshot_taken_at,
                    adjusted_skill_rank = EXCLUDED.adjusted_skill_rank,
                    weighted_rank = EXCLUDED.weighted_rank,
                    fc_rate_rank = EXCLUDED.fc_rate_rank,
                    total_score_rank = EXCLUDED.total_score_rank,
                    max_score_percent_rank = EXCLUDED.max_score_percent_rank,
                    adjusted_skill_rating = EXCLUDED.adjusted_skill_rating,
                    weighted_rating = EXCLUDED.weighted_rating,
                    fc_rate = EXCLUDED.fc_rate,
                    total_score = EXCLUDED.total_score,
                    max_score_percent = EXCLUDED.max_score_percent,
                    songs_played = EXCLUDED.songs_played,
                    coverage = EXCLUDED.coverage,
                    full_combo_count = EXCLUDED.full_combo_count,
                    raw_max_score_percent = EXCLUDED.raw_max_score_percent,
                    raw_weighted_rating = EXCLUDED.raw_weighted_rating,
                    raw_skill_rating = EXCLUDED.raw_skill_rating,
                    schema_version = EXCLUDED.schema_version";
            c.Parameters.AddWithValue("today", today);
            c.Parameters.AddWithValue("instrument", Instrument);
            c.ExecuteNonQuery();
        }

        UpsertRankHistorySnapshotStats(conn, tx, today);

        int rows;
        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = "SELECT COUNT(*) FROM rank_history WHERE instrument = @instrument AND snapshot_date = @today";
            c.Parameters.AddWithValue("instrument", Instrument);
            c.Parameters.AddWithValue("today", today);
            rows = Convert.ToInt32(c.ExecuteScalar());
        }
        tx.Commit();

        if (cleanupRetention)
            CleanupRankHistoryRetention(retentionDays);

        return rows;
    }

    public int CleanupRankHistoryRetention(int retentionDays = 365, int batchSize = RankHistoryCleanupBatchSize, int maxBatches = RankHistoryCleanupMaxBatches)
    {
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));
        if (maxBatches <= 0) throw new ArgumentOutOfRangeException(nameof(maxBatches));

        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-retentionDays);
        int totalDeleted = 0;

        using var conn = _ds.OpenConnection();
        for (int batch = 0; batch < maxBatches; batch++)
        {
            using var tx = conn.BeginTransaction();
            using var c = conn.CreateCommand();
            c.Transaction = tx;
            c.CommandTimeout = 0;
            c.CommandText = @"
                WITH doomed AS (
                    SELECT account_id, snapshot_date
                    FROM (
                        SELECT
                            account_id,
                            snapshot_date,
                            ROW_NUMBER() OVER (
                                PARTITION BY account_id
                                ORDER BY snapshot_date DESC
                            ) AS recency
                        FROM rank_history
                        WHERE instrument = @instrument
                          AND snapshot_date <= @cutoff
                    ) ranked
                    WHERE recency > 1
                    ORDER BY snapshot_date ASC, account_id ASC
                    LIMIT @batchSize
                )
                DELETE FROM rank_history rh
                USING doomed d
                WHERE rh.instrument = @instrument
                  AND rh.account_id = d.account_id
                  AND rh.snapshot_date = d.snapshot_date";
            c.Parameters.AddWithValue("instrument", Instrument);
            c.Parameters.AddWithValue("cutoff", cutoff);
            c.Parameters.AddWithValue("batchSize", batchSize);

            int deleted = c.ExecuteNonQuery();
            tx.Commit();
            totalDeleted += deleted;

            if (deleted == 0)
                break;
        }

        if (totalDeleted > 0)
            _log.LogDebug("Trimmed {DeletedCount} retained rank_history rows for {Instrument}.", totalDeleted, Instrument);

        return totalDeleted;
    }

    public (List<AccountRankingDto> Entries, int TotalCount) GetAccountRankings(string rankBy = "adjusted", int page = 1, int pageSize = 50)
    {
        var (col, dir) = RankByColumn(rankBy);
        using var conn = _ds.OpenConnection();

        var total = GetRankedAccountCountWithBackfill(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT account_id, songs_played, total_charted_songs, coverage, raw_skill_rating, adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank, fc_rate, fc_rate_rank, total_score, total_score_rank, max_score_percent, max_score_percent_rank, avg_accuracy, full_combo_count, avg_stars, best_rank, avg_rank, computed_at, raw_max_score_percent, raw_weighted_rating FROM account_rankings WHERE instrument = @instrument ORDER BY {col} {dir} LIMIT @limit OFFSET @offset";
        cmd.Parameters.AddWithValue("instrument", Instrument);
        cmd.Parameters.AddWithValue("limit", pageSize);
        cmd.Parameters.AddWithValue("offset", (page - 1) * pageSize);
        var list = new List<AccountRankingDto>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadAccountRanking(r));
        return (list, total);
    }
    public AccountRankingDto? GetAccountRanking(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id, songs_played, total_charted_songs, coverage, raw_skill_rating, adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank, fc_rate, fc_rate_rank, total_score, total_score_rank, max_score_percent, max_score_percent_rank, avg_accuracy, full_combo_count, avg_stars, best_rank, avg_rank, computed_at, raw_max_score_percent, raw_weighted_rating FROM account_rankings WHERE instrument = @instrument AND account_id = @accountId"; cmd.Parameters.AddWithValue("instrument", Instrument); cmd.Parameters.AddWithValue("accountId", accountId); using var r = cmd.ExecuteReader(); return r.Read() ? ReadAccountRanking(r) : null; }

    public (List<AccountRankingDto> Above, AccountRankingDto? Self, List<AccountRankingDto> Below) GetAccountRankingNeighborhood(string accountId, int radius = 5, string rankBy = "totalscore")
    {
        var self = GetAccountRanking(accountId); if (self is null) return (new(), null, new());
        if (radius <= 0) return (new(), self, new());
        var (rankCol, _) = RankByColumn(rankBy);
        var selfRankValue = InstrumentDatabase.GetRankValue(self, rankBy);
        using var conn = _ds.OpenConnection();
        var above = new List<AccountRankingDto>();
        using (var cmd = conn.CreateCommand()) { cmd.CommandText = $"SELECT account_id, songs_played, total_charted_songs, coverage, raw_skill_rating, adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank, fc_rate, fc_rate_rank, total_score, total_score_rank, max_score_percent, max_score_percent_rank, avg_accuracy, full_combo_count, avg_stars, best_rank, avg_rank, computed_at, raw_max_score_percent, raw_weighted_rating FROM account_rankings WHERE instrument = @instrument AND {rankCol} < @selfRank ORDER BY {rankCol} DESC LIMIT @radius"; cmd.Parameters.AddWithValue("instrument", Instrument); cmd.Parameters.AddWithValue("selfRank", selfRankValue); cmd.Parameters.AddWithValue("radius", radius); using var r = cmd.ExecuteReader(); while (r.Read()) above.Add(ReadAccountRanking(r)); }
        above.Reverse();
        var below = new List<AccountRankingDto>();
        using (var cmd = conn.CreateCommand()) { cmd.CommandText = $"SELECT account_id, songs_played, total_charted_songs, coverage, raw_skill_rating, adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank, fc_rate, fc_rate_rank, total_score, total_score_rank, max_score_percent, max_score_percent_rank, avg_accuracy, full_combo_count, avg_stars, best_rank, avg_rank, computed_at, raw_max_score_percent, raw_weighted_rating FROM account_rankings WHERE instrument = @instrument AND {rankCol} > @selfRank ORDER BY {rankCol} ASC LIMIT @radius"; cmd.Parameters.AddWithValue("instrument", Instrument); cmd.Parameters.AddWithValue("selfRank", selfRankValue); cmd.Parameters.AddWithValue("radius", radius); using var r = cmd.ExecuteReader(); while (r.Read()) below.Add(ReadAccountRanking(r)); }
        return (above, self, below);
    }

    public List<RankHistoryDto> GetRankHistory(string accountId, int days = 30)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT
                h.snapshot_date,
                h.snapshot_taken_at,
                h.adjusted_skill_rank,
                h.weighted_rank,
                h.fc_rate_rank,
                h.total_score_rank,
                h.max_score_percent_rank,
                h.adjusted_skill_rating,
                h.weighted_rating,
                h.fc_rate,
                h.total_score,
                h.max_score_percent,
                h.songs_played,
                h.coverage,
                h.full_combo_count,
                h.raw_max_score_percent,
                h.raw_weighted_rating,
                h.raw_skill_rating,
                rhs.total_charted_songs,
                rhs.ranked_account_count
            FROM (
                SELECT DISTINCT ON (snapshot_date)
                    snapshot_date,
                    snapshot_taken_at,
                    adjusted_skill_rank,
                    weighted_rank,
                    fc_rate_rank,
                    total_score_rank,
                    max_score_percent_rank,
                    adjusted_skill_rating,
                    weighted_rating,
                    fc_rate,
                    total_score,
                    max_score_percent,
                    songs_played,
                    coverage,
                    full_combo_count,
                    raw_max_score_percent,
                    raw_weighted_rating,
                    raw_skill_rating
                FROM rank_history
                WHERE instrument = @instrument AND account_id = @accountId AND snapshot_date >= @cutoff
                ORDER BY snapshot_date DESC
            ) h
            LEFT JOIN {RankHistorySnapshotStatsTable} rhs
                ON rhs.instrument = @instrument AND rhs.snapshot_date = h.snapshot_date
            ORDER BY h.snapshot_date";
        cmd.Parameters.AddWithValue("instrument", Instrument);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("cutoff", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-days)));

        var list = new List<RankHistoryDto>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new RankHistoryDto
            {
                SnapshotDate = r.GetDateTime(0).ToString("yyyy-MM-dd"),
                SnapshotTakenAt = r.IsDBNull(1) ? null : r.GetFieldValue<DateTime>(1).ToString("o"),
                AdjustedSkillRank = r.GetInt32(2),
                WeightedRank = r.GetInt32(3),
                FcRateRank = r.GetInt32(4),
                TotalScoreRank = r.GetInt32(5),
                MaxScorePercentRank = r.GetInt32(6),
                AdjustedSkillRating = r.IsDBNull(7) ? null : r.GetDouble(7),
                WeightedRating = r.IsDBNull(8) ? null : r.GetDouble(8),
                FcRate = r.IsDBNull(9) ? null : r.GetDouble(9),
                TotalScore = r.IsDBNull(10) ? null : r.GetInt64(10),
                MaxScorePercent = r.IsDBNull(11) ? null : r.GetDouble(11),
                SongsPlayed = r.IsDBNull(12) ? null : r.GetInt32(12),
                Coverage = r.IsDBNull(13) ? null : r.GetDouble(13),
                FullComboCount = r.IsDBNull(14) ? null : r.GetInt32(14),
                RawMaxScorePercent = r.IsDBNull(15) ? null : r.GetDouble(15),
                RawWeightedRating = r.IsDBNull(16) ? null : r.GetDouble(16),
                RawSkillRating = r.IsDBNull(17) ? null : r.GetDouble(17),
                TotalChartedSongs = r.IsDBNull(18) ? null : r.GetInt32(18),
                RankedAccountCount = r.IsDBNull(19) ? null : r.GetInt32(19),
            });
        return list;
    }

    /// <summary>Returns rank history deltas for a specific leeway bucket over a date range.</summary>
    public List<RankHistoryDeltaDto> GetRankHistoryDeltas(string accountId, double leewayBucket, int days = 30)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT snapshot_date, rank_adjusted, rank_weighted, rank_fcrate, rank_totalscore, rank_maxscore
            FROM rank_history_deltas
            WHERE instrument = @instrument AND account_id = @accountId
              AND leeway_bucket = @bucket AND snapshot_date >= @cutoff
            ORDER BY snapshot_date";
        cmd.Parameters.AddWithValue("instrument", Instrument);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("bucket", (float)leewayBucket);
        cmd.Parameters.AddWithValue("cutoff", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-days)));
        var list = new List<RankHistoryDeltaDto>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new RankHistoryDeltaDto
            {
                SnapshotDate = r.GetDateTime(0).ToString("yyyy-MM-dd"),
                AdjustedRankDelta = r.IsDBNull(1) ? 0 : r.GetInt32(1),
                WeightedRankDelta = r.IsDBNull(2) ? 0 : r.GetInt32(2),
                FcRateRankDelta = r.IsDBNull(3) ? 0 : r.GetInt32(3),
                TotalScoreRankDelta = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                MaxScoreRankDelta = r.IsDBNull(5) ? 0 : r.GetInt32(5),
            });
        return list;
    }

    public int GetRankedAccountCount()
    {
        using var conn = _ds.OpenConnection();
        return GetRankedAccountCountWithBackfill(conn);
    }

    public List<(string AccountId, double AdjustedSkillRating, int SongsPlayed, int AdjustedSkillRank)> GetAllRankingSummaries()
    {
        using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT account_id, adjusted_skill_rating, songs_played, adjusted_skill_rank FROM account_rankings WHERE instrument = @instrument";
        cmd.Parameters.AddWithValue("instrument", Instrument);
        var list = new List<(string, double, int, int)>(); using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetDouble(1), r.GetInt32(2), r.GetInt32(3)));
        return list;
    }

    public List<(string AccountId, double AdjustedSkillRating, double WeightedRating, double FcRate, long TotalScore, double MaxScorePercent, int SongsPlayed, int FullComboCount)> GetAllRankingSummariesFull()
    {
        using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT account_id, adjusted_skill_rating, weighted_rating, fc_rate, total_score, max_score_percent, songs_played, full_combo_count FROM account_rankings WHERE instrument = @instrument";
        cmd.Parameters.AddWithValue("instrument", Instrument);
        var list = new List<(string, double, double, double, long, double, int, int)>(); using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3), r.GetInt64(4), r.GetDouble(5), r.GetInt32(6), r.GetInt32(7)));
        return list;
    }

    public void PreWarmRankingsBatch(IReadOnlyCollection<string> accountIds) { /* No-op for PG — MVCC handles concurrency, no cache needed */ }

    public void Checkpoint() { }

    // ── Private helpers ──────────────────────────────────────────────

    private void SyncOverlayEntries(
        string songId,
        IReadOnlyList<LeaderboardEntry> entries,
        DateTime now,
        NpgsqlConnection conn,
        NpgsqlTransaction tx)
    {
        var scrapeAccountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var overlayRowsByAccount = new Dictionary<string, LeaderboardOverlayWriteRow>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (IsScrapeSource(entry.Source))
            {
                scrapeAccountIds.Add(entry.AccountId);
                overlayRowsByAccount.Remove(entry.AccountId);
                continue;
            }

            if (!TryCreateOverlayWriteRow(songId, entry, now, out var overlayRow))
                continue;

            if (scrapeAccountIds.Contains(entry.AccountId))
                continue;

            if (!overlayRowsByAccount.TryGetValue(entry.AccountId, out var existingRow)
                || overlayRow.SourcePriority > existingRow.SourcePriority
                || overlayRow.SourcePriority == existingRow.SourcePriority)
            {
                overlayRowsByAccount[entry.AccountId] = overlayRow;
            }
        }

        if (scrapeAccountIds.Count > 0)
            DeleteOverlayEntries(conn, tx, songId, scrapeAccountIds);

        if (overlayRowsByAccount.Count > 0)
            UpsertOverlayEntries(conn, tx, overlayRowsByAccount.Values);
    }

    private static bool IsScrapeSource(string? source) =>
        string.IsNullOrWhiteSpace(source) || string.Equals(source, "scrape", StringComparison.OrdinalIgnoreCase);

    private bool TryCreateOverlayWriteRow(string songId, LeaderboardEntry entry, DateTime now, [NotNullWhen(true)] out LeaderboardOverlayWriteRow? row)
    {
        if (!TryGetOverlayMetadata(entry.Source, out var sourcePriority, out var overlayReason))
        {
            row = default;
            return false;
        }

        row = new LeaderboardOverlayWriteRow(
            songId,
            Instrument,
            entry.AccountId,
            entry.Score,
            entry.Accuracy,
            entry.IsFullCombo,
            entry.Stars,
            entry.Season,
            entry.Percentile,
            entry.Rank,
            entry.Source ?? "overlay",
            entry.Difficulty,
            entry.ApiRank,
            entry.EndTime,
            SerializeBandMembers(entry),
            entry.BandScore,
            entry.BaseScore,
            entry.InstrumentBonus,
            entry.OverdriveBonus,
            entry.InstrumentCombo,
            now,
            now,
            sourcePriority,
            overlayReason);
        return true;
    }

    private static bool TryGetOverlayMetadata(string? source, out int sourcePriority, out string overlayReason)
    {
        switch (source)
        {
            case "backfill":
                sourcePriority = OverlayPriorityPreservedCurrent;
                overlayReason = "preserved-backfill";
                return true;
            case "refresh":
                sourcePriority = OverlayPriorityPreservedCurrent;
                overlayReason = "preserved-refresh";
                return true;
            case "neighbor":
                sourcePriority = OverlayPriorityNeighbor;
                overlayReason = "preserved-neighbor";
                return true;
            default:
                sourcePriority = 0;
                overlayReason = string.Empty;
                return false;
        }
    }

    private void DeleteOverlayEntries(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string songId,
        IReadOnlyCollection<string> accountIds)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DELETE FROM {LeaderboardEntriesOverlayTable} WHERE song_id = @songId AND instrument = @instrument AND account_id = ANY(@accountIds)";
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", Instrument);
        cmd.Parameters.AddWithValue("accountIds", accountIds.ToArray());
        cmd.ExecuteNonQuery();
    }

    private static void UpsertOverlayEntries(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        IEnumerable<LeaderboardOverlayWriteRow> rows)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            INSERT INTO {LeaderboardEntriesOverlayTable}
            (song_id, instrument, account_id, score, accuracy, is_full_combo, stars, season, percentile, rank, source, difficulty, api_rank, end_time, band_members_json, band_score, base_score, instrument_bonus, overdrive_bonus, instrument_combo, first_seen_at, last_updated_at, source_priority, overlay_reason)
            VALUES
            (@songId, @instrument, @accountId, @score, @accuracy, @isFullCombo, @stars, @season, @percentile, @rank, @source, @difficulty, @apiRank, @endTime, @bandMembersJson, @bandScore, @baseScore, @instrumentBonus, @overdriveBonus, @instrumentCombo, @firstSeenAt, @lastUpdatedAt, @sourcePriority, @overlayReason)
            ON CONFLICT (song_id, instrument, account_id) DO UPDATE SET
                score = CASE WHEN EXCLUDED.source_priority >= {LeaderboardEntriesOverlayTable}.source_priority THEN EXCLUDED.score ELSE {LeaderboardEntriesOverlayTable}.score END,
                accuracy = CASE WHEN EXCLUDED.source_priority >= {LeaderboardEntriesOverlayTable}.source_priority THEN EXCLUDED.accuracy ELSE {LeaderboardEntriesOverlayTable}.accuracy END,
                is_full_combo = CASE WHEN EXCLUDED.source_priority >= {LeaderboardEntriesOverlayTable}.source_priority THEN EXCLUDED.is_full_combo ELSE {LeaderboardEntriesOverlayTable}.is_full_combo END,
                stars = CASE WHEN EXCLUDED.source_priority >= {LeaderboardEntriesOverlayTable}.source_priority THEN EXCLUDED.stars ELSE {LeaderboardEntriesOverlayTable}.stars END,
                season = CASE WHEN EXCLUDED.source_priority >= {LeaderboardEntriesOverlayTable}.source_priority THEN EXCLUDED.season ELSE {LeaderboardEntriesOverlayTable}.season END,
                percentile = CASE WHEN EXCLUDED.source_priority >= {LeaderboardEntriesOverlayTable}.source_priority THEN EXCLUDED.percentile ELSE {LeaderboardEntriesOverlayTable}.percentile END,
                rank = CASE WHEN EXCLUDED.source_priority >= {LeaderboardEntriesOverlayTable}.source_priority THEN EXCLUDED.rank ELSE {LeaderboardEntriesOverlayTable}.rank END,
                source = CASE WHEN EXCLUDED.source_priority >= {LeaderboardEntriesOverlayTable}.source_priority THEN EXCLUDED.source ELSE {LeaderboardEntriesOverlayTable}.source END,
                difficulty = CASE WHEN EXCLUDED.source_priority >= {LeaderboardEntriesOverlayTable}.source_priority THEN EXCLUDED.difficulty ELSE {LeaderboardEntriesOverlayTable}.difficulty END,
                api_rank = CASE WHEN EXCLUDED.source_priority >= {LeaderboardEntriesOverlayTable}.source_priority THEN EXCLUDED.api_rank ELSE {LeaderboardEntriesOverlayTable}.api_rank END,
                end_time = CASE WHEN EXCLUDED.source_priority >= {LeaderboardEntriesOverlayTable}.source_priority THEN EXCLUDED.end_time ELSE {LeaderboardEntriesOverlayTable}.end_time END,
                band_members_json = CASE WHEN EXCLUDED.source_priority >= {LeaderboardEntriesOverlayTable}.source_priority THEN EXCLUDED.band_members_json ELSE {LeaderboardEntriesOverlayTable}.band_members_json END,
                band_score = CASE WHEN EXCLUDED.source_priority >= {LeaderboardEntriesOverlayTable}.source_priority THEN EXCLUDED.band_score ELSE {LeaderboardEntriesOverlayTable}.band_score END,
                base_score = CASE WHEN EXCLUDED.source_priority >= {LeaderboardEntriesOverlayTable}.source_priority THEN EXCLUDED.base_score ELSE {LeaderboardEntriesOverlayTable}.base_score END,
                instrument_bonus = CASE WHEN EXCLUDED.source_priority >= {LeaderboardEntriesOverlayTable}.source_priority THEN EXCLUDED.instrument_bonus ELSE {LeaderboardEntriesOverlayTable}.instrument_bonus END,
                overdrive_bonus = CASE WHEN EXCLUDED.source_priority >= {LeaderboardEntriesOverlayTable}.source_priority THEN EXCLUDED.overdrive_bonus ELSE {LeaderboardEntriesOverlayTable}.overdrive_bonus END,
                instrument_combo = CASE WHEN EXCLUDED.source_priority >= {LeaderboardEntriesOverlayTable}.source_priority THEN EXCLUDED.instrument_combo ELSE {LeaderboardEntriesOverlayTable}.instrument_combo END,
                first_seen_at = LEAST({LeaderboardEntriesOverlayTable}.first_seen_at, EXCLUDED.first_seen_at),
                last_updated_at = CASE WHEN EXCLUDED.source_priority >= {LeaderboardEntriesOverlayTable}.source_priority THEN EXCLUDED.last_updated_at ELSE {LeaderboardEntriesOverlayTable}.last_updated_at END,
                source_priority = GREATEST({LeaderboardEntriesOverlayTable}.source_priority, EXCLUDED.source_priority),
                overlay_reason = CASE WHEN EXCLUDED.source_priority >= {LeaderboardEntriesOverlayTable}.source_priority THEN EXCLUDED.overlay_reason ELSE {LeaderboardEntriesOverlayTable}.overlay_reason END
            """;
        cmd.Parameters.Add("songId", NpgsqlDbType.Text);
        cmd.Parameters.Add("instrument", NpgsqlDbType.Text);
        cmd.Parameters.Add("accountId", NpgsqlDbType.Text);
        cmd.Parameters.Add("score", NpgsqlDbType.Integer);
        cmd.Parameters.Add("accuracy", NpgsqlDbType.Integer);
        cmd.Parameters.Add("isFullCombo", NpgsqlDbType.Boolean);
        cmd.Parameters.Add("stars", NpgsqlDbType.Integer);
        cmd.Parameters.Add("season", NpgsqlDbType.Integer);
        cmd.Parameters.Add("percentile", NpgsqlDbType.Double);
        cmd.Parameters.Add("rank", NpgsqlDbType.Integer);
        cmd.Parameters.Add("source", NpgsqlDbType.Text);
        cmd.Parameters.Add("difficulty", NpgsqlDbType.Integer);
        cmd.Parameters.Add("apiRank", NpgsqlDbType.Integer);
        cmd.Parameters.Add("endTime", NpgsqlDbType.Text);
        cmd.Parameters.Add("bandMembersJson", NpgsqlDbType.Jsonb);
        cmd.Parameters.Add("bandScore", NpgsqlDbType.Integer);
        cmd.Parameters.Add("baseScore", NpgsqlDbType.Integer);
        cmd.Parameters.Add("instrumentBonus", NpgsqlDbType.Integer);
        cmd.Parameters.Add("overdriveBonus", NpgsqlDbType.Integer);
        cmd.Parameters.Add("instrumentCombo", NpgsqlDbType.Text);
        cmd.Parameters.Add("firstSeenAt", NpgsqlDbType.TimestampTz);
        cmd.Parameters.Add("lastUpdatedAt", NpgsqlDbType.TimestampTz);
        cmd.Parameters.Add("sourcePriority", NpgsqlDbType.Integer);
        cmd.Parameters.Add("overlayReason", NpgsqlDbType.Text);
        cmd.Prepare();

        foreach (var row in rows)
        {
            cmd.Parameters["songId"].Value = row.SongId;
            cmd.Parameters["instrument"].Value = row.Instrument;
            cmd.Parameters["accountId"].Value = row.AccountId;
            cmd.Parameters["score"].Value = row.Score;
            cmd.Parameters["accuracy"].Value = row.Accuracy;
            cmd.Parameters["isFullCombo"].Value = row.IsFullCombo;
            cmd.Parameters["stars"].Value = row.Stars;
            cmd.Parameters["season"].Value = row.Season;
            cmd.Parameters["percentile"].Value = row.Percentile;
            cmd.Parameters["rank"].Value = row.Rank;
            cmd.Parameters["source"].Value = row.Source;
            cmd.Parameters["difficulty"].Value = row.Difficulty;
            cmd.Parameters["apiRank"].Value = row.ApiRank > 0 ? row.ApiRank : DBNull.Value;
            cmd.Parameters["endTime"].Value = (object?)row.EndTime ?? DBNull.Value;
            cmd.Parameters["bandMembersJson"].Value = (object?)row.BandMembersJson ?? DBNull.Value;
            cmd.Parameters["bandScore"].Value = row.BandScore.HasValue ? row.BandScore.Value : DBNull.Value;
            cmd.Parameters["baseScore"].Value = row.BaseScore.HasValue ? row.BaseScore.Value : DBNull.Value;
            cmd.Parameters["instrumentBonus"].Value = row.InstrumentBonus.HasValue ? row.InstrumentBonus.Value : DBNull.Value;
            cmd.Parameters["overdriveBonus"].Value = row.OverdriveBonus.HasValue ? row.OverdriveBonus.Value : DBNull.Value;
            cmd.Parameters["instrumentCombo"].Value = (object?)row.InstrumentCombo ?? DBNull.Value;
            cmd.Parameters["firstSeenAt"].Value = row.FirstSeenAt;
            cmd.Parameters["lastUpdatedAt"].Value = row.LastUpdatedAt;
            cmd.Parameters["sourcePriority"].Value = row.SourcePriority;
            cmd.Parameters["overlayReason"].Value = row.OverlayReason;
            cmd.ExecuteNonQuery();
        }
    }

    private (List<LeaderboardEntryDto> Entries, int TotalCount) GetCurrentStateLeaderboardCore(
        string songId,
        int? top,
        int offset,
        int? maxScore,
        bool includeTotalCount)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        var limitClause = top.HasValue ? $"LIMIT {top.Value} OFFSET {offset}" : "";
        cmd.CommandText = BuildCurrentStateLeaderboardSql(includeTotalCount, maxScore.HasValue, limitClause);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", Instrument);
        if (maxScore.HasValue)
            cmd.Parameters.AddWithValue("maxScore", maxScore.Value);

        var list = new List<LeaderboardEntryDto>();
        var total = 0;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadEntryDto(reader));
            if (includeTotalCount && total == 0)
                total = reader.GetInt32(10);
        }

        return (list, total);
    }

    private static string BuildCurrentStateLeaderboardSql(bool includeTotalCount, bool hasMaxScore, string limitClause)
    {
        var totalProjection = includeTotalCount ? "total_count" : "0";
        var totalComputation = includeTotalCount ? ", COUNT(*) OVER ()::INT AS total_count" : string.Empty;
        var scoreFilter = hasMaxScore ? "WHERE score <= @maxScore" : string.Empty;

        return $"""
            WITH active_snapshot AS (
                SELECT active_snapshot_id
                FROM {LeaderboardSnapshotStateTable}
                WHERE song_id = @songId
                  AND instrument = @instrument
                  AND is_finalized = TRUE
                  AND active_snapshot_id IS NOT NULL
            ),
            base_rows AS (
                SELECT account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, end_time, api_rank, source, first_seen_at
                FROM leaderboard_entries
                WHERE song_id = @songId
                  AND instrument = @instrument
                  AND NOT EXISTS (SELECT 1 FROM active_snapshot)
                UNION ALL
                SELECT account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, end_time, api_rank, source, first_seen_at
                FROM {LeaderboardEntriesSnapshotTable}
                WHERE song_id = @songId
                  AND instrument = @instrument
                  AND snapshot_id = (SELECT active_snapshot_id FROM active_snapshot)
            ),
            candidate_rows AS (
                SELECT account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, end_time, api_rank, source, first_seen_at,
                       1 AS origin_precedence,
                       0 AS source_priority
                FROM base_rows
                UNION ALL
                SELECT account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, end_time, api_rank, source, first_seen_at,
                       0 AS origin_precedence,
                       source_priority
                FROM {LeaderboardEntriesOverlayTable}
                WHERE song_id = @songId
                  AND instrument = @instrument
            ),
            resolved_rows AS (
                SELECT DISTINCT ON (account_id)
                    account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, end_time, api_rank, source, first_seen_at
                FROM candidate_rows
                ORDER BY account_id, origin_precedence ASC, source_priority DESC
            ),
            ranked_rows AS (
                SELECT account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, end_time,
                       ROW_NUMBER() OVER (ORDER BY score DESC, COALESCE(end_time, first_seen_at::TEXT) ASC) AS rank
                       {totalComputation},
                       api_rank,
                       source
                FROM resolved_rows
                {scoreFilter}
            )
            SELECT account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, end_time, rank,
                   {totalProjection} AS total_count,
                   api_rank,
                   source
            FROM ranked_rows
            ORDER BY rank
            {limitClause}
            """;
    }

    private static string BuildCurrentStateNeighborhoodSql() =>
        $"""
            WITH current_state AS (
                SELECT account_id, score, rank
                FROM ({BuildCurrentStateLeaderboardSql(includeTotalCount: false, hasMaxScore: false, limitClause: string.Empty)}) current_rows
            )
            SELECT account_id, rank, score
            FROM current_state
            WHERE rank BETWEEN @lo AND @hi
              AND account_id != @exclude
            ORDER BY rank
            """;

    private static string BuildCurrentStateSongIdsForAccountSql() =>
        $"""
            WITH active_snapshots AS (
                SELECT song_id, active_snapshot_id
                FROM {LeaderboardSnapshotStateTable}
                WHERE instrument = @instrument
                  AND is_finalized = TRUE
                  AND active_snapshot_id IS NOT NULL
            ),
            base_rows AS (
                SELECT le.song_id, le.account_id, le.score, le.accuracy, le.is_full_combo, le.stars, le.season, le.difficulty, le.percentile, le.end_time, le.rank, le.api_rank, le.source, le.first_seen_at,
                       1 AS origin_precedence,
                       0 AS source_priority
                FROM leaderboard_entries le
                WHERE le.instrument = @instrument
                  AND NOT EXISTS (
                      SELECT 1
                      FROM active_snapshots snapshot
                      WHERE snapshot.song_id = le.song_id
                  )
                UNION ALL
                SELECT snapshot.song_id, snapshot.account_id, snapshot.score, snapshot.accuracy, snapshot.is_full_combo, snapshot.stars, snapshot.season, snapshot.difficulty, snapshot.percentile, snapshot.end_time, snapshot.rank, snapshot.api_rank, snapshot.source, snapshot.first_seen_at,
                       1 AS origin_precedence,
                       0 AS source_priority
                FROM {LeaderboardEntriesSnapshotTable} snapshot
                JOIN active_snapshots active ON active.song_id = snapshot.song_id AND active.active_snapshot_id = snapshot.snapshot_id
                WHERE snapshot.instrument = @instrument
                UNION ALL
                SELECT overlay.song_id, overlay.account_id, overlay.score, overlay.accuracy, overlay.is_full_combo, overlay.stars, overlay.season, overlay.difficulty, overlay.percentile, overlay.end_time, overlay.rank, overlay.api_rank, overlay.source, overlay.first_seen_at,
                       0 AS origin_precedence,
                       overlay.source_priority
                FROM {LeaderboardEntriesOverlayTable} overlay
                WHERE overlay.instrument = @instrument
            ),
            resolved_rows AS (
                SELECT DISTINCT ON (song_id, account_id)
                    song_id, account_id
                FROM base_rows
                ORDER BY song_id, account_id, origin_precedence ASC, source_priority DESC
            )
            SELECT song_id
            FROM resolved_rows
            WHERE account_id = @accountId
            ORDER BY song_id
            """;

    private static string BuildCurrentStateAllSongCountsSql() =>
        $"""
            SELECT song_id, COUNT(*)::INT
            FROM ({BuildCurrentStateResolvedEntriesSql()}) resolved_rows
            GROUP BY song_id
            """;

    private static string BuildCurrentStateResolvedEntriesSql() =>
        $"""
            WITH active_snapshots AS (
                SELECT song_id, active_snapshot_id
                FROM {LeaderboardSnapshotStateTable}
                WHERE instrument = @instrument
                  AND is_finalized = TRUE
                  AND active_snapshot_id IS NOT NULL
            ),
            base_rows AS (
                SELECT le.song_id, le.account_id, le.score, le.accuracy, le.is_full_combo, le.stars,
                       le.rank, le.api_rank, le.first_seen_at, le.end_time,
                       1 AS origin_precedence,
                       0 AS source_priority
                FROM leaderboard_entries le
                WHERE le.instrument = @instrument
                  AND NOT EXISTS (
                      SELECT 1
                      FROM active_snapshots snapshot
                      WHERE snapshot.song_id = le.song_id
                  )
                UNION ALL
                SELECT snapshot.song_id, snapshot.account_id, snapshot.score, snapshot.accuracy, snapshot.is_full_combo, snapshot.stars,
                       snapshot.rank, snapshot.api_rank, snapshot.first_seen_at, snapshot.end_time,
                       1 AS origin_precedence,
                       0 AS source_priority
                FROM {LeaderboardEntriesSnapshotTable} snapshot
                JOIN active_snapshots active ON active.song_id = snapshot.song_id AND active.active_snapshot_id = snapshot.snapshot_id
                WHERE snapshot.instrument = @instrument
                UNION ALL
                SELECT overlay.song_id, overlay.account_id, overlay.score, overlay.accuracy, overlay.is_full_combo, overlay.stars,
                       overlay.rank, overlay.api_rank, overlay.first_seen_at, overlay.end_time,
                       0 AS origin_precedence,
                       overlay.source_priority
                FROM {LeaderboardEntriesOverlayTable} overlay
                WHERE overlay.instrument = @instrument
            ),
            resolved_rows AS (
                SELECT DISTINCT ON (song_id, account_id)
                    song_id, account_id, score, accuracy, is_full_combo, stars, rank, api_rank, first_seen_at, end_time
                FROM base_rows
                ORDER BY song_id, account_id, origin_precedence ASC, source_priority DESC
            )
            SELECT song_id, account_id, score, accuracy, is_full_combo, stars, rank, api_rank, first_seen_at, end_time
            FROM resolved_rows
            """;

    private static string BuildCurrentStatePlayerScoresSql(bool hasSongIdFilter)
    {
        var songFilter = hasSongIdFilter ? "AND song_id = @songId" : string.Empty;
        return $"""
            WITH active_snapshots AS (
                SELECT song_id, active_snapshot_id
                FROM {LeaderboardSnapshotStateTable}
                WHERE instrument = @instrument
                  AND is_finalized = TRUE
                  AND active_snapshot_id IS NOT NULL
            ),
            base_rows AS (
                SELECT le.song_id, le.account_id, le.score, le.accuracy, le.is_full_combo, le.stars, le.season, le.difficulty, le.percentile, le.end_time, le.rank, le.api_rank, le.source, le.first_seen_at,
                       1 AS origin_precedence,
                       0 AS source_priority
                FROM leaderboard_entries le
                WHERE le.instrument = @instrument
                  AND NOT EXISTS (
                      SELECT 1
                      FROM active_snapshots snapshot
                      WHERE snapshot.song_id = le.song_id
                  )
                UNION ALL
                SELECT snapshot.song_id, snapshot.account_id, snapshot.score, snapshot.accuracy, snapshot.is_full_combo, snapshot.stars, snapshot.season, snapshot.difficulty, snapshot.percentile, snapshot.end_time, snapshot.rank, snapshot.api_rank, snapshot.source, snapshot.first_seen_at,
                       1 AS origin_precedence,
                       0 AS source_priority
                FROM {LeaderboardEntriesSnapshotTable} snapshot
                JOIN active_snapshots active ON active.song_id = snapshot.song_id AND active.active_snapshot_id = snapshot.snapshot_id
                WHERE snapshot.instrument = @instrument
                UNION ALL
                SELECT overlay.song_id, overlay.account_id, overlay.score, overlay.accuracy, overlay.is_full_combo, overlay.stars, overlay.season, overlay.difficulty, overlay.percentile, overlay.end_time, overlay.rank, overlay.api_rank, overlay.source, overlay.first_seen_at,
                       0 AS origin_precedence,
                       overlay.source_priority
                FROM {LeaderboardEntriesOverlayTable} overlay
                WHERE overlay.instrument = @instrument
            ),
            resolved_rows AS (
                SELECT DISTINCT ON (song_id, account_id)
                    song_id, account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, end_time, rank, api_rank, first_seen_at
                FROM base_rows
                ORDER BY song_id, account_id, origin_precedence ASC, source_priority DESC
            ),
            ranked_rows AS (
                SELECT song_id, account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, end_time,
                       ROW_NUMBER() OVER (PARTITION BY song_id ORDER BY score DESC, COALESCE(end_time, first_seen_at::TEXT) ASC) AS rank,
                       api_rank
                FROM resolved_rows
            )
            SELECT song_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, end_time, rank, api_rank
            FROM ranked_rows
            WHERE account_id = @accountId {songFilter}
            ORDER BY song_id
            """;
    }

    private static string BuildCurrentStatePlayerScoresForSongsSql(int songIdCount)
    {
        var parameterNames = Enumerable.Range(0, songIdCount).Select(i => $"@s{i}");
        return BuildCurrentStatePlayerScoresSql(hasSongIdFilter: false)
            .Replace("WHERE account_id = @accountId", $"WHERE account_id = @accountId AND song_id IN ({string.Join(",", parameterNames)})");
    }

    private sealed record LeaderboardOverlayWriteRow(
        string SongId,
        string Instrument,
        string AccountId,
        int Score,
        int Accuracy,
        bool IsFullCombo,
        int Stars,
        int Season,
        double Percentile,
        int Rank,
        string Source,
        int Difficulty,
        int ApiRank,
        string? EndTime,
        string? BandMembersJson,
        int? BandScore,
        int? BaseScore,
        int? InstrumentBonus,
        int? OverdriveBonus,
        string? InstrumentCombo,
        DateTime FirstSeenAt,
        DateTime LastUpdatedAt,
        int SourcePriority,
        string OverlayReason);

    private LeaderboardEntryDto ReadEntryDto(NpgsqlDataReader r) => new() { AccountId = r.GetString(0), Score = r.GetInt32(1), Accuracy = r.IsDBNull(2) ? 0 : r.GetInt32(2), IsFullCombo = !r.IsDBNull(3) && r.GetBoolean(3), Stars = r.IsDBNull(4) ? 0 : r.GetInt32(4), Season = r.IsDBNull(5) ? 0 : r.GetInt32(5), Difficulty = r.IsDBNull(6) ? 0 : r.GetInt32(6), Percentile = r.IsDBNull(7) ? 0 : r.GetDouble(7), EndTime = r.IsDBNull(8) ? null : r.GetString(8), Rank = r.FieldCount > 9 && !r.IsDBNull(9) ? (int)r.GetInt64(9) : 0, ApiRank = r.FieldCount > 11 && !r.IsDBNull(11) ? r.GetInt32(11) : 0, Source = r.FieldCount > 12 && !r.IsDBNull(12) ? r.GetString(12) : "scrape" };
    private PlayerScoreDto ReadPlayerScore(NpgsqlDataReader r) => new() { SongId = r.GetString(0), Instrument = Instrument, Score = r.GetInt32(1), Accuracy = r.IsDBNull(2) ? 0 : r.GetInt32(2), IsFullCombo = !r.IsDBNull(3) && r.GetBoolean(3), Stars = r.IsDBNull(4) ? 0 : r.GetInt32(4), Season = r.IsDBNull(5) ? 0 : r.GetInt32(5), Difficulty = r.IsDBNull(6) ? 0 : r.GetInt32(6), Percentile = r.IsDBNull(7) ? 0 : r.GetDouble(7), EndTime = r.IsDBNull(8) ? null : r.GetString(8), Rank = r.IsDBNull(9) ? 0 : r.GetInt32(9), ApiRank = r.IsDBNull(10) ? 0 : r.GetInt32(10) };
    private static AccountRankingDto ReadAccountRanking(NpgsqlDataReader r) => new() { AccountId = r.GetString(0), SongsPlayed = r.GetInt32(1), TotalChartedSongs = r.GetInt32(2), Coverage = r.GetDouble(3), RawSkillRating = r.GetDouble(4), AdjustedSkillRating = r.GetDouble(5), AdjustedSkillRank = r.GetInt32(6), WeightedRating = r.GetDouble(7), WeightedRank = r.GetInt32(8), FcRate = r.GetDouble(9), FcRateRank = r.GetInt32(10), TotalScore = r.GetInt64(11), TotalScoreRank = r.GetInt32(12), MaxScorePercent = r.GetDouble(13), MaxScorePercentRank = r.GetInt32(14), AvgAccuracy = r.GetDouble(15), FullComboCount = r.GetInt32(16), AvgStars = r.GetDouble(17), BestRank = r.GetInt32(18), AvgRank = r.GetDouble(19), ComputedAt = r.GetDateTime(20).ToString("o"), RawMaxScorePercent = r.IsDBNull(21) ? null : r.GetDouble(21), RawWeightedRating = r.IsDBNull(22) ? null : r.GetDouble(22) };
    private static (string Column, string Direction) RankByColumn(string rankBy) => rankBy switch { "weighted" => ("weighted_rank", "ASC"), "fcrate" => ("fc_rate_rank", "ASC"), "totalscore" => ("total_score_rank", "ASC"), "maxscore" => ("max_score_percent_rank", "ASC"), _ => ("adjusted_skill_rank", "ASC") };

    /// <summary>Maps instrument name to the partition table name for TRUNCATE operations.</summary>
    private string GetPartitionName(string parentTable) => Instrument switch
    {
        "Solo_Guitar" => $"{parentTable}_solo_guitar",
        "Solo_Bass" => $"{parentTable}_solo_bass",
        "Solo_Drums" => $"{parentTable}_solo_drums",
        "Solo_Vocals" => $"{parentTable}_solo_vocals",
        "Solo_PeripheralGuitar" => $"{parentTable}_pro_guitar",
        "Solo_PeripheralBass" => $"{parentTable}_pro_bass",
        "Solo_PeripheralVocals" => $"{parentTable}_pro_vocals",
        "Solo_PeripheralCymbals" => $"{parentTable}_pro_cymbals",
        "Solo_PeripheralDrums" => $"{parentTable}_pro_drums",
        _ => throw new ArgumentException($"Unknown instrument: {Instrument}"),
    };

    // ── Leeway-aware ranking queries ─────────────────────────────────

        private bool ResolveRankHistoryHasPrimaryKey()
        {
            using var conn = _ds.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conrelid = to_regclass(@tableName)
                      AND contype = 'p'
                )";
            cmd.Parameters.AddWithValue("tableName", "public.rank_history");
            var hasPrimaryKey = (bool)(cmd.ExecuteScalar() ?? false);
            if (!hasPrimaryKey)
                _log.LogWarning("rank_history has no primary key; using legacy same-day snapshot path for {Instrument}.", Instrument);
            return hasPrimaryKey;
        }

    /// <summary>Quantizes a leeway value to the nearest 0.1 bucket (floor). Returns 99.0 for null (unfiltered).</summary>
    public static double QuantizeBucket(double? leeway)
    {
        if (leeway is null) return 99.0;
        double v = Math.Clamp(leeway.Value, -5.0, 5.0);
        return Math.Round(Math.Floor(v * 10) / 10.0, 1);
    }

    /// <summary>
    /// Compatibility wrapper for legacy leeway-aware callers.
    /// Global rankings no longer vary by leeway and always return canonical results.
    /// </summary>
    public (List<AccountRankingDto> Entries, int TotalCount) GetRankingsAtLeeway(
        double leewayBucket, string rankBy = "adjusted", int page = 1, int pageSize = 50)
    {
        _ = leewayBucket;
        return GetAccountRankings(rankBy, page, pageSize);
    }

    /// <summary>
    /// Compatibility wrapper for legacy leeway-aware callers.
    /// Global rankings no longer vary by leeway and always return canonical results.
    /// </summary>
    public AccountRankingDto? GetAccountRankingAtLeeway(string accountId, double leewayBucket, string rankBy = "adjusted")
    {
        _ = leewayBucket;
        _ = rankBy;
        return GetAccountRanking(accountId);
    }

    // ── Ranking deltas ───────────────────────────────────────────────

    /// <summary>DTO for aggregate metrics at a given threshold for a single account.</summary>
    public sealed class AccountAggregateMetrics
    {
        public int SongsPlayed;
        public double AdjustedSkill;
        public double Weighted;
        public double FcRate;
        public long TotalScore;
        public double MaxScorePct;
        public int FullComboCount;
        public double AvgAccuracy;
        public int BestRank;
        public double Coverage;
    }

    /// <summary>
    /// Returns accounts with at least one leaderboard entry in the score band
    /// (base_threshold × max_score, max_threshold × max_score].
    /// For each such entry, returns the leeway bucket at which it becomes valid.
    /// </summary>
    public List<(string AccountId, double ActivationLeeway)> GetBandEntries(double baseThreshold = 0.95, double maxThreshold = 1.05)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT le.account_id,
                ROUND(((le.score::double precision / ss.max_score) - 1.0) * 1000) / 10.0 AS activation_leeway
            FROM leaderboard_entries le
            JOIN song_stats ss ON ss.song_id = le.song_id AND ss.instrument = le.instrument
            WHERE le.instrument = @instrument
              AND ss.max_score IS NOT NULL AND ss.max_score > 0
              AND le.score > CAST(ss.max_score * @baseTh AS INTEGER)
              AND le.score <= CAST(ss.max_score * @maxTh AS INTEGER)
              AND ss.entry_count > 0
              AND COALESCE(NULLIF(le.api_rank, 0), le.rank) > 0";
        cmd.Parameters.AddWithValue("instrument", Instrument);
        cmd.Parameters.AddWithValue("baseTh", baseThreshold);
        cmd.Parameters.AddWithValue("maxTh", maxThreshold);
        var results = new List<(string, double)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            results.Add((r.GetString(0), r.GetDouble(1)));
        return results;
    }

    /// <summary>
    /// Compute aggregate metrics at a given threshold for a specific set of accounts.
    /// Returns the Bayesian-adjusted metrics (same formulas as ComputeAccountRankings)
    /// but without inserting or ranking — just returns the metric values.
    /// </summary>
    public Dictionary<string, AccountAggregateMetrics> ComputeMetricsAtThreshold(
        double threshold, HashSet<string> accountIds, int totalChartedSongs,
        int credibilityThreshold, double populationMedian)
    {
        if (accountIds.Count == 0) return new();
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        // Same CTE as ComputeAccountRankings but filtered to specific accounts,
        // no INSERT, no ROW_NUMBER ranking, just returns aggregate metrics.
        cmd.CommandText =
            "WITH ValidEntries AS (" +
            "SELECT le.song_id, le.account_id, le.score, le.accuracy, le.is_full_combo, le.stars, " +
            "COALESCE(NULLIF(le.api_rank, 0), le.rank) AS effective_rank, ss.entry_count, ss.log_weight, ss.max_score " +
            "FROM leaderboard_entries le JOIN song_stats ss ON ss.song_id = le.song_id AND ss.instrument = le.instrument " +
            "WHERE le.instrument = @instrument AND le.score <= COALESCE(CAST(ss.max_score * @threshold AS INTEGER), le.score + 1) " +
            "AND ss.entry_count > 0 AND COALESCE(NULLIF(le.api_rank, 0), le.rank) > 0 AND le.account_id = ANY(@accounts) " +
            "UNION ALL " +
            "SELECT vso.song_id, vso.account_id, vso.score, COALESCE(vso.accuracy, 0), COALESCE(vso.is_full_combo, false), COALESCE(vso.stars, 0), " +
            "(SELECT COUNT(*) + 1 FROM leaderboard_entries le2 JOIN song_stats ss2 ON ss2.song_id = le2.song_id AND ss2.instrument = le2.instrument " +
            "WHERE le2.song_id = vso.song_id AND le2.instrument = @instrument AND le2.score > vso.score " +
            "AND le2.score <= COALESCE(CAST(ss2.max_score * @threshold AS INTEGER), le2.score + 1) AND le2.account_id != vso.account_id), " +
            "ss.entry_count, ss.log_weight, ss.max_score " +
            "FROM valid_score_overrides vso JOIN song_stats ss ON ss.song_id = vso.song_id AND ss.instrument = vso.instrument " +
            "WHERE vso.instrument = @instrument AND ss.entry_count > 0 AND vso.account_id = ANY(@accounts)), " +
            "Aggregated AS (" +
            "SELECT v.account_id, COUNT(*) AS songs_played, " +
            "CAST(COUNT(*) AS DOUBLE PRECISION) / @totalCharted AS coverage, " +
            "AVG(CAST(v.effective_rank AS DOUBLE PRECISION) / v.entry_count) AS raw_skill_rating, " +
            "SUM((CAST(v.effective_rank AS DOUBLE PRECISION) / v.entry_count) * v.log_weight) / NULLIF(SUM(v.log_weight), 0) AS weighted_rating, " +
            "CAST(SUM(CASE WHEN v.is_full_combo THEN 1 ELSE 0 END) AS DOUBLE PRECISION) / @totalCharted AS fc_rate, " +
            "SUM(v.score) AS total_score, " +
            "AVG(CASE WHEN v.max_score IS NOT NULL AND v.max_score > 0 THEN LEAST(CAST(v.score AS DOUBLE PRECISION) / v.max_score, @threshold) ELSE NULL END) AS max_score_percent, " +
            "AVG(v.accuracy) AS avg_accuracy, " +
            "SUM(CASE WHEN v.is_full_combo THEN 1 ELSE 0 END) AS full_combo_count, " +
            "MIN(v.effective_rank) AS best_rank " +
            "FROM ValidEntries v GROUP BY v.account_id) " +
            "SELECT account_id, songs_played, coverage, " +
            "(songs_played * raw_skill_rating + @m * @C) / (songs_played + @m) AS adjusted_skill, " +
            "(songs_played * COALESCE(weighted_rating, 1.0) + @m * @C) / (songs_played + @m) AS weighted, " +
            "fc_rate, total_score, " +
            "(songs_played * COALESCE(max_score_percent, 0.5) + @m * @C) / (songs_played + @m) AS max_score_pct, " +
            "avg_accuracy, full_combo_count, best_rank " +
            "FROM Aggregated";
        cmd.Parameters.AddWithValue("instrument", Instrument);
        cmd.Parameters.AddWithValue("threshold", threshold);
        cmd.Parameters.AddWithValue("totalCharted", totalChartedSongs);
        cmd.Parameters.AddWithValue("m", credibilityThreshold);
        cmd.Parameters.AddWithValue("C", populationMedian);
        cmd.Parameters.AddWithValue("accounts", accountIds.ToArray());

        var result = new Dictionary<string, AccountAggregateMetrics>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result[r.GetString(0)] = new AccountAggregateMetrics
            {
                SongsPlayed = r.GetInt32(1),
                Coverage = r.GetDouble(2),
                AdjustedSkill = r.GetDouble(3),
                Weighted = r.GetDouble(4),
                FcRate = r.GetDouble(5),
                TotalScore = r.GetInt64(6),
                MaxScorePct = r.GetDouble(7),
                AvgAccuracy = r.GetDouble(8),
                FullComboCount = r.GetInt32(9),
                BestRank = r.GetInt32(10),
            };
        }
        return result;
    }

    /// <summary>
    /// Compute aggregate metrics for a set of accounts with NO threshold filter (unfiltered).
    /// All entries are included regardless of score vs CHOpt max.
    /// </summary>
    public Dictionary<string, AccountAggregateMetrics> ComputeMetricsUnfiltered(
        HashSet<string> accountIds, int totalChartedSongs,
        int credibilityThreshold, double populationMedian)
    {
        if (accountIds.Count == 0) return new();
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText =
            "WITH ValidEntries AS (" +
            "SELECT le.song_id, le.account_id, le.score, le.accuracy, le.is_full_combo, le.stars, " +
            "COALESCE(NULLIF(le.api_rank, 0), le.rank) AS effective_rank, ss.entry_count, ss.log_weight, ss.max_score " +
            "FROM leaderboard_entries le JOIN song_stats ss ON ss.song_id = le.song_id AND ss.instrument = le.instrument " +
            "WHERE le.instrument = @instrument AND ss.entry_count > 0 AND COALESCE(NULLIF(le.api_rank, 0), le.rank) > 0 " +
            "AND le.account_id = ANY(@accounts)), " +
            "Aggregated AS (" +
            "SELECT v.account_id, COUNT(*) AS songs_played, " +
            "CAST(COUNT(*) AS DOUBLE PRECISION) / @totalCharted AS coverage, " +
            "AVG(CAST(v.effective_rank AS DOUBLE PRECISION) / v.entry_count) AS raw_skill_rating, " +
            "SUM((CAST(v.effective_rank AS DOUBLE PRECISION) / v.entry_count) * v.log_weight) / NULLIF(SUM(v.log_weight), 0) AS weighted_rating, " +
            "CAST(SUM(CASE WHEN v.is_full_combo THEN 1 ELSE 0 END) AS DOUBLE PRECISION) / @totalCharted AS fc_rate, " +
            "SUM(v.score) AS total_score, " +
            "AVG(CASE WHEN v.max_score IS NOT NULL AND v.max_score > 0 THEN CAST(v.score AS DOUBLE PRECISION) / v.max_score ELSE NULL END) AS max_score_percent, " +
            "AVG(v.accuracy) AS avg_accuracy, " +
            "SUM(CASE WHEN v.is_full_combo THEN 1 ELSE 0 END) AS full_combo_count, " +
            "MIN(v.effective_rank) AS best_rank " +
            "FROM ValidEntries v GROUP BY v.account_id) " +
            "SELECT account_id, songs_played, coverage, " +
            "(songs_played * raw_skill_rating + @m * @C) / (songs_played + @m) AS adjusted_skill, " +
            "(songs_played * COALESCE(weighted_rating, 1.0) + @m * @C) / (songs_played + @m) AS weighted, " +
            "fc_rate, total_score, " +
            "(songs_played * COALESCE(max_score_percent, 0.5) + @m * @C) / (songs_played + @m) AS max_score_pct, " +
            "avg_accuracy, full_combo_count, best_rank " +
            "FROM Aggregated";
        cmd.Parameters.AddWithValue("instrument", Instrument);
        cmd.Parameters.AddWithValue("totalCharted", totalChartedSongs);
        cmd.Parameters.AddWithValue("m", credibilityThreshold);
        cmd.Parameters.AddWithValue("C", populationMedian);
        cmd.Parameters.AddWithValue("accounts", accountIds.ToArray());

        var result = new Dictionary<string, AccountAggregateMetrics>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result[r.GetString(0)] = new AccountAggregateMetrics
            {
                SongsPlayed = r.GetInt32(1),
                Coverage = r.GetDouble(2),
                AdjustedSkill = r.GetDouble(3),
                Weighted = r.GetDouble(4),
                FcRate = r.GetDouble(5),
                TotalScore = r.GetInt64(6),
                MaxScorePct = r.GetDouble(7),
                AvgAccuracy = r.GetDouble(8),
                FullComboCount = r.GetInt32(9),
                BestRank = r.GetInt32(10),
            };
        }
        return result;
    }

    /// <summary>Truncates ranking_deltas partition for this instrument.</summary>
    public void TruncateRankingDeltas()
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"TRUNCATE {GetPartitionName("ranking_deltas")}";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Batch-inserts ranking delta rows for this instrument.</summary>
    public void WriteRankingDeltas(IReadOnlyList<(string AccountId, double LeewayBucket,
        int SongsPlayed, double AdjustedSkill, double Weighted, double FcRate, long TotalScore,
        double MaxScorePct, int FullComboCount, double AvgAccuracy, int BestRank, double Coverage)> deltas)
    {
        if (deltas.Count == 0) return;
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO ranking_deltas (account_id, instrument, leeway_bucket, songs_played, adjusted_skill, " +
            "weighted, fc_rate, total_score, max_score_pct, full_combo_count, avg_accuracy, best_rank, coverage) " +
            "VALUES (@aid, @inst, @bucket, @songs, @adj, @wgt, @fc, @ts, @ms, @fcc, @acc, @br, @cov)";
        cmd.Parameters.Add("aid", NpgsqlTypes.NpgsqlDbType.Text);
        cmd.Parameters.Add("inst", NpgsqlTypes.NpgsqlDbType.Text);
        cmd.Parameters.Add("bucket", NpgsqlTypes.NpgsqlDbType.Real);
        cmd.Parameters.Add("songs", NpgsqlTypes.NpgsqlDbType.Integer);
        cmd.Parameters.Add("adj", NpgsqlTypes.NpgsqlDbType.Double);
        cmd.Parameters.Add("wgt", NpgsqlTypes.NpgsqlDbType.Double);
        cmd.Parameters.Add("fc", NpgsqlTypes.NpgsqlDbType.Double);
        cmd.Parameters.Add("ts", NpgsqlTypes.NpgsqlDbType.Bigint);
        cmd.Parameters.Add("ms", NpgsqlTypes.NpgsqlDbType.Double);
        cmd.Parameters.Add("fcc", NpgsqlTypes.NpgsqlDbType.Integer);
        cmd.Parameters.Add("acc", NpgsqlTypes.NpgsqlDbType.Double);
        cmd.Parameters.Add("br", NpgsqlTypes.NpgsqlDbType.Integer);
        cmd.Parameters.Add("cov", NpgsqlTypes.NpgsqlDbType.Double);
        cmd.Prepare();

        foreach (var d in deltas)
        {
            cmd.Parameters["aid"].Value = d.AccountId;
            cmd.Parameters["inst"].Value = Instrument;
            cmd.Parameters["bucket"].Value = (float)d.LeewayBucket;
            cmd.Parameters["songs"].Value = d.SongsPlayed;
            cmd.Parameters["adj"].Value = d.AdjustedSkill;
            cmd.Parameters["wgt"].Value = d.Weighted;
            cmd.Parameters["fc"].Value = d.FcRate;
            cmd.Parameters["ts"].Value = d.TotalScore;
            cmd.Parameters["ms"].Value = d.MaxScorePct;
            cmd.Parameters["fcc"].Value = d.FullComboCount;
            cmd.Parameters["acc"].Value = d.AvgAccuracy;
            cmd.Parameters["br"].Value = d.BestRank;
            cmd.Parameters["cov"].Value = d.Coverage;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Returns all ranking delta rows for this instrument.</summary>
    public List<(string AccountId, double LeewayBucket, int SongsPlayed, double AdjustedSkill,
        double Weighted, double FcRate, long TotalScore, double MaxScorePct, int FullComboCount)> GetAllRankingDeltas()
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT account_id, leeway_bucket, songs_played, adjusted_skill, weighted, fc_rate, total_score, max_score_pct, full_combo_count FROM ranking_deltas WHERE instrument = @instrument";
        cmd.Parameters.AddWithValue("instrument", Instrument);
        var list = new List<(string, double, int, double, double, double, long, double, int)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetString(0), r.GetFloat(1), r.GetInt32(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), r.GetInt64(6), r.GetDouble(7), r.GetInt32(8)));
        return list;
    }

    /// <summary>
    /// Returns today's rank history deltas for a specific account on this instrument.
    /// Each row is (bucket, delta_adjusted, delta_weighted, delta_fcrate, delta_totalscore, delta_maxscore).
    /// </summary>
    public List<(double LeewayBucket, int DeltaAdj, int DeltaWgt, int DeltaFc, int DeltaTs, int DeltaMs)> GetTodayRankDeltas(string accountId)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT leeway_bucket, rank_adjusted, rank_weighted, rank_fcrate, rank_totalscore, rank_maxscore FROM rank_history_deltas WHERE instrument = @instrument AND account_id = @accountId AND snapshot_date = @today ORDER BY leeway_bucket";
        cmd.Parameters.AddWithValue("instrument", Instrument);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("today", today);
        var list = new List<(double, int, int, int, int, int)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetFloat(0), r.IsDBNull(1) ? 0 : r.GetInt32(1), r.IsDBNull(2) ? 0 : r.GetInt32(2), r.IsDBNull(3) ? 0 : r.GetInt32(3), r.IsDBNull(4) ? 0 : r.GetInt32(4), r.IsDBNull(5) ? 0 : r.GetInt32(5)));
        return list;
    }

    /// <summary>
    /// Snapshot rank history deltas for each leeway bucket where ranks differ from base.
    /// Covers all ranked accounts. Uses COPY BINARY for efficient bulk writes.
    /// Deltas use "default 0 for missing days" semantics — no carry-forward needed.
    /// </summary>
    public void SnapshotRankHistoryDeltas(int retentionDays = 365)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var cutoff = today.AddDays(-retentionDays);
        using var conn = _ds.OpenConnection();

        // Find distinct buckets in ranking_deltas for this instrument
        var buckets = new List<double>();
        using (var c = conn.CreateCommand())
        {
            c.CommandText = "SELECT DISTINCT leeway_bucket FROM ranking_deltas WHERE instrument = @instrument ORDER BY leeway_bucket";
            c.Parameters.AddWithValue("instrument", Instrument);
            using var r = c.ExecuteReader();
            while (r.Read()) buckets.Add(r.GetFloat(0));
        }
        if (buckets.Count == 0) return;

        using var tx = conn.BeginTransaction();

        // Delete today's existing deltas for this instrument (idempotent re-run)
        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = "DELETE FROM rank_history_deltas WHERE instrument = @instrument AND snapshot_date = @today";
            c.Parameters.AddWithValue("instrument", Instrument);
            c.Parameters.AddWithValue("today", today);
            c.ExecuteNonQuery();
        }

        // Collect all non-zero delta rows across buckets
        var allRows = new List<(string Aid, float Bucket, int Da, int Dw, int Df, int Dt, int Dm)>();

        foreach (var bucket in buckets)
        {
            using var rankCmd = conn.CreateCommand();
            rankCmd.Transaction = tx;
            rankCmd.CommandText = @"
                WITH effective AS (
                    SELECT ar.account_id,
                        ar.adjusted_skill_rank AS base_adj,
                        ar.weighted_rank AS base_wgt,
                        ar.fc_rate_rank AS base_fc,
                        ar.total_score_rank AS base_ts,
                        ar.max_score_percent_rank AS base_ms,
                        ROW_NUMBER() OVER (ORDER BY COALESCE(rd.adjusted_skill, ar.adjusted_skill_rating) ASC, ar.songs_played DESC, ar.account_id ASC) AS eff_adj,
                        ROW_NUMBER() OVER (ORDER BY COALESCE(rd.weighted, ar.weighted_rating) ASC, ar.songs_played DESC, ar.account_id ASC) AS eff_wgt,
                        ROW_NUMBER() OVER (ORDER BY COALESCE(rd.fc_rate, ar.fc_rate) DESC, ar.songs_played DESC, ar.account_id ASC) AS eff_fc,
                        ROW_NUMBER() OVER (ORDER BY COALESCE(rd.total_score, ar.total_score) DESC, ar.songs_played DESC, ar.account_id ASC) AS eff_ts,
                        ROW_NUMBER() OVER (ORDER BY COALESCE(rd.max_score_pct, ar.max_score_percent) DESC, ar.songs_played DESC, ar.account_id ASC) AS eff_ms
                    FROM account_rankings ar
                    LEFT JOIN ranking_deltas rd ON rd.account_id = ar.account_id
                        AND rd.instrument = ar.instrument AND rd.leeway_bucket = @bucket
                    WHERE ar.instrument = @instrument
                )
                SELECT account_id,
                    (eff_adj - base_adj)::int AS da,
                    (eff_wgt - base_wgt)::int AS dw,
                    (eff_fc - base_fc)::int AS df,
                    (eff_ts - base_ts)::int AS dt,
                    (eff_ms - base_ms)::int AS dm
                FROM effective
                WHERE eff_adj != base_adj OR eff_wgt != base_wgt OR eff_fc != base_fc
                      OR eff_ts != base_ts OR eff_ms != base_ms";
            rankCmd.Parameters.AddWithValue("bucket", (float)bucket);
            rankCmd.Parameters.AddWithValue("instrument", Instrument);

            using var r = rankCmd.ExecuteReader();
            while (r.Read())
                allRows.Add((r.GetString(0), (float)bucket, r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5)));
        }

        // COPY BINARY bulk insert all collected rows
        if (allRows.Count > 0)
        {
            using var writer = conn.BeginBinaryImport(
                "COPY rank_history_deltas (account_id, instrument, snapshot_date, leeway_bucket, rank_adjusted, rank_weighted, rank_fcrate, rank_totalscore, rank_maxscore) FROM STDIN (FORMAT BINARY)");
            foreach (var row in allRows)
            {
                writer.StartRow();
                writer.Write(row.Aid, NpgsqlTypes.NpgsqlDbType.Text);
                writer.Write(Instrument, NpgsqlTypes.NpgsqlDbType.Text);
                writer.Write(today, NpgsqlTypes.NpgsqlDbType.Date);
                writer.Write(row.Bucket, NpgsqlTypes.NpgsqlDbType.Real);
                writer.Write(row.Da, NpgsqlTypes.NpgsqlDbType.Integer);
                writer.Write(row.Dw, NpgsqlTypes.NpgsqlDbType.Integer);
                writer.Write(row.Df, NpgsqlTypes.NpgsqlDbType.Integer);
                writer.Write(row.Dt, NpgsqlTypes.NpgsqlDbType.Integer);
                writer.Write(row.Dm, NpgsqlTypes.NpgsqlDbType.Integer);
            }
            writer.Complete();
        }

        // Simple date-based retention (deltas use "default 0 for missing" semantics)
        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = "DELETE FROM rank_history_deltas WHERE instrument = @instrument AND snapshot_date < @cutoff";
            c.Parameters.AddWithValue("instrument", Instrument);
            c.Parameters.AddWithValue("cutoff", cutoff);
            c.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// Compatibility wrapper for legacy leeway-aware callers.
    /// Rank history no longer varies by leeway and always returns canonical snapshots.
    /// </summary>
    public List<RankHistoryDto> GetRankHistoryAtLeeway(string accountId, double leewayBucket, int days = 30)
    {
        _ = leewayBucket;
        return GetRankHistory(accountId, days);
    }

    // ── Ranking delta tiers (interval-compressed deltas) ─────────────

    /// <summary>Total number of regular leeway buckets (-4.9 to +5.0 in 0.1 steps).</summary>
    internal const int RegularBucketCount = 100; // indices 0..99
    /// <summary>Bucket index for the unfiltered sentinel (leeway 99.0).</summary>
    internal const int UnfilteredBucketIndex = 100;
    /// <summary>Total bucket indices including the unfiltered sentinel.</summary>
    internal const int TotalBucketCount = 101; // 0..100

    /// <summary>
    /// Converts a leeway bucket value (e.g. -4.9, 0.0, 5.0, 99.0) to a stable integer index.
    /// Regular buckets: index = round((bucket + 4.9) * 10), range 0–99.
    /// Unfiltered sentinel (99.0): index = 100.
    /// </summary>
    public static int BucketToIndex(double bucket)
    {
        if (bucket >= 98.0) return UnfilteredBucketIndex; // sentinel
        int idx = (int)Math.Round((bucket + 4.9) * 10);
        return Math.Clamp(idx, 0, RegularBucketCount - 1);
    }

    /// <summary>
    /// Converts a bucket index back to the leeway bucket value.
    /// Index 0 → -4.9, index 99 → 5.0, index 100 → 99.0 (unfiltered).
    /// </summary>
    public static double IndexToBucket(int index) =>
        index >= UnfilteredBucketIndex ? 99.0 : Math.Round(index / 10.0 - 4.9, 1);

    /// <summary>Truncates ranking_delta_tiers partition for this instrument.</summary>
    public void TruncateRankingDeltaTiers()
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"TRUNCATE {GetPartitionName("ranking_delta_tiers")}";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Write interval-compressed ranking delta tiers using COPY binary.
    /// Each tier represents a half-open bucket index range [start, end) with constant metrics.
    /// </summary>
    public void WriteRankingDeltaTiersBulk(IReadOnlyList<(string AccountId, int StartBucketIdx, int EndBucketIdx,
        int SongsPlayed, double AdjustedSkill, double Weighted, double FcRate, long TotalScore,
        double MaxScorePct, int FullComboCount, double AvgAccuracy, int BestRank, double Coverage)> tiers)
    {
        if (tiers.Count == 0) return;
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();

        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = @"
                CREATE TEMP TABLE _rdt_staging (
                    account_id TEXT, instrument TEXT, start_bucket_idx SMALLINT, end_bucket_idx SMALLINT,
                    songs_played INTEGER, adjusted_skill REAL, weighted REAL,
                    fc_rate REAL, total_score BIGINT, max_score_pct REAL,
                    full_combo_count INTEGER, avg_accuracy REAL, best_rank INTEGER,
                    coverage REAL
                ) ON COMMIT DROP";
            c.ExecuteNonQuery();
        }

        using (var writer = conn.BeginBinaryImport(
            "COPY _rdt_staging (account_id, instrument, start_bucket_idx, end_bucket_idx, " +
            "songs_played, adjusted_skill, weighted, fc_rate, total_score, max_score_pct, " +
            "full_combo_count, avg_accuracy, best_rank, coverage) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var t in tiers)
            {
                writer.StartRow();
                writer.Write(t.AccountId, NpgsqlDbType.Text);
                writer.Write(Instrument, NpgsqlDbType.Text);
                writer.Write((short)t.StartBucketIdx, NpgsqlDbType.Smallint);
                writer.Write((short)t.EndBucketIdx, NpgsqlDbType.Smallint);
                writer.Write(t.SongsPlayed, NpgsqlDbType.Integer);
                writer.Write((float)t.AdjustedSkill, NpgsqlDbType.Real);
                writer.Write((float)t.Weighted, NpgsqlDbType.Real);
                writer.Write((float)t.FcRate, NpgsqlDbType.Real);
                writer.Write(t.TotalScore, NpgsqlDbType.Bigint);
                writer.Write((float)t.MaxScorePct, NpgsqlDbType.Real);
                writer.Write(t.FullComboCount, NpgsqlDbType.Integer);
                writer.Write((float)t.AvgAccuracy, NpgsqlDbType.Real);
                writer.Write(t.BestRank, NpgsqlDbType.Integer);
                writer.Write((float)t.Coverage, NpgsqlDbType.Real);
            }
            writer.Complete();
        }

        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = "INSERT INTO ranking_delta_tiers SELECT * FROM _rdt_staging";
            c.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// Compress dense per-bucket delta results into interval tiers.
    /// Consecutive buckets with identical metrics for the same account are merged into
    /// a single tier with a half-open index range [start, end).
    /// </summary>
    public static List<(string AccountId, int StartBucketIdx, int EndBucketIdx,
        int SongsPlayed, double AdjustedSkill, double Weighted, double FcRate, long TotalScore,
        double MaxScorePct, int FullComboCount, double AvgAccuracy, int BestRank, double Coverage)>
        CompressDeltasToTiers(
            IReadOnlyList<(string AccountId, double LeewayBucket,
                int SongsPlayed, double AdjustedSkill, double Weighted, double FcRate, long TotalScore,
                double MaxScorePct, int FullComboCount, double AvgAccuracy, int BestRank, double Coverage)> deltas)
    {
        if (deltas.Count == 0) return [];

        // Group by account, sort by bucket index within each group
        var byAccount = new Dictionary<string, List<(int Idx, int SongsPlayed, double AdjustedSkill,
            double Weighted, double FcRate, long TotalScore, double MaxScorePct,
            int FullComboCount, double AvgAccuracy, int BestRank, double Coverage)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in deltas)
        {
            if (!byAccount.TryGetValue(d.AccountId, out var list))
            {
                list = [];
                byAccount[d.AccountId] = list;
            }
            list.Add((BucketToIndex(d.LeewayBucket), d.SongsPlayed, d.AdjustedSkill,
                d.Weighted, d.FcRate, d.TotalScore, d.MaxScorePct,
                d.FullComboCount, d.AvgAccuracy, d.BestRank, d.Coverage));
        }

        var tiers = new List<(string, int, int, int, double, double, double, long, double, int, double, int, double)>();

        foreach (var (accountId, entries) in byAccount)
        {
            entries.Sort((a, b) => a.Idx.CompareTo(b.Idx));

            int startIdx = entries[0].Idx;
            var cur = entries[0];

            for (int i = 1; i < entries.Count; i++)
            {
                var e = entries[i];
                // Merge if consecutive index and identical metrics
                if (e.Idx == entries[i - 1].Idx + 1 &&
                    e.SongsPlayed == cur.SongsPlayed &&
                    Math.Abs(e.AdjustedSkill - cur.AdjustedSkill) < 1e-9 &&
                    Math.Abs(e.Weighted - cur.Weighted) < 1e-9 &&
                    Math.Abs(e.FcRate - cur.FcRate) < 1e-9 &&
                    e.TotalScore == cur.TotalScore &&
                    Math.Abs(e.MaxScorePct - cur.MaxScorePct) < 1e-9 &&
                    e.FullComboCount == cur.FullComboCount)
                {
                    continue; // extend current interval
                }

                // Emit current interval
                tiers.Add((accountId, startIdx, e.Idx, cur.SongsPlayed, cur.AdjustedSkill,
                    cur.Weighted, cur.FcRate, cur.TotalScore, cur.MaxScorePct,
                    cur.FullComboCount, cur.AvgAccuracy, cur.BestRank, cur.Coverage));
                startIdx = e.Idx;
                cur = e;
            }

            // Emit final interval (end = last index + 1 for half-open)
            tiers.Add((accountId, startIdx, entries[^1].Idx + 1, cur.SongsPlayed, cur.AdjustedSkill,
                cur.Weighted, cur.FcRate, cur.TotalScore, cur.MaxScorePct,
                cur.FullComboCount, cur.AvgAccuracy, cur.BestRank, cur.Coverage));
        }

        return tiers;
    }

    // ── Materialized valid entries for ranking pipeline ───────────────

    /// <summary>
    /// Materializes the <c>leaderboard_entries × song_stats</c> join into a temp table
    /// that is reused by base-ranking computation, band-entry discovery, and per-bucket
    /// delta metrics — eliminating 100+ redundant joins per instrument.
    ///
    /// The temp table (<c>_valid_entries</c>) lives for the lifetime of <paramref name="conn"/>
    /// and should be dropped explicitly or by closing the connection when done.
    ///
    /// Also creates <c>_valid_entries_overrides</c> for valid_score_overrides data.
    /// </summary>
    public void MaterializeValidEntries(NpgsqlConnection conn, double baseThreshold)
    {
        // Drop any leftover tables from a previous call on this connection
        using (var c = conn.CreateCommand())
        {
            c.CommandText = "DROP TABLE IF EXISTS _valid_entries; DROP TABLE IF EXISTS _valid_entries_overrides";
            c.ExecuteNonQuery();
        }

        // Materialize the main join — this is the most expensive scan,
        // done once and reused for everything downstream.
        using (var c = conn.CreateCommand())
        {
            c.CommandTimeout = 0;
            c.CommandText = @"
                CREATE TEMP TABLE _valid_entries AS
                SELECT le.song_id, le.account_id, le.score, le.accuracy, le.is_full_combo, le.stars,
                       COALESCE(NULLIF(le.api_rank, 0), le.rank) AS effective_rank,
                       ss.entry_count, ss.log_weight, ss.max_score
                FROM leaderboard_entries le
                JOIN song_stats ss ON ss.song_id = le.song_id AND ss.instrument = le.instrument
                WHERE le.instrument = @instrument
                  AND ss.entry_count > 0
                  AND COALESCE(NULLIF(le.api_rank, 0), le.rank) > 0";
            c.Parameters.AddWithValue("instrument", Instrument);
            c.ExecuteNonQuery();
        }

        // Create indexes for downstream queries
        using (var c = conn.CreateCommand())
        {
            c.CommandTimeout = 0;
            c.CommandText = @"
                CREATE INDEX ON _valid_entries (account_id);
                CREATE INDEX ON _valid_entries (max_score, score) WHERE max_score IS NOT NULL AND max_score > 0";
            c.ExecuteNonQuery();
        }

        // Materialize valid_score_overrides with song_stats joined
        using (var c = conn.CreateCommand())
        {
            c.CommandText = @"
                CREATE TEMP TABLE _valid_entries_overrides AS
                SELECT vso.song_id, vso.account_id, vso.score,
                       COALESCE(vso.accuracy, 0) AS accuracy,
                       COALESCE(vso.is_full_combo, false) AS is_full_combo,
                       COALESCE(vso.stars, 0) AS stars,
                       ss.entry_count, ss.log_weight, ss.max_score
                FROM valid_score_overrides vso
                JOIN song_stats ss ON ss.song_id = vso.song_id AND ss.instrument = vso.instrument
                WHERE vso.instrument = @instrument AND ss.entry_count > 0";
            c.Parameters.AddWithValue("instrument", Instrument);
            c.ExecuteNonQuery();
        }

        using (var c = conn.CreateCommand())
        {
            c.CommandTimeout = 0;
            c.CommandText = "ANALYZE _valid_entries; ANALYZE _valid_entries_overrides";
            c.ExecuteNonQuery();
        }
    }

    public void MaterializeCurrentStateValidEntries(NpgsqlConnection conn, double baseThreshold)
    {
        using (var c = conn.CreateCommand())
        {
            c.CommandText = "DROP TABLE IF EXISTS _valid_entries; DROP TABLE IF EXISTS _valid_entries_overrides";
            c.ExecuteNonQuery();
        }

        using (var c = conn.CreateCommand())
        {
            c.CommandTimeout = 0;
            c.CommandText = $"""
                CREATE TEMP TABLE _valid_entries AS
                WITH current_rows AS (
                    {BuildCurrentStateResolvedEntriesSql()}
                )
                SELECT current_rows.song_id, current_rows.account_id, current_rows.score, current_rows.accuracy, current_rows.is_full_combo, current_rows.stars,
                       COALESCE(NULLIF(current_rows.api_rank, 0), current_rows.rank) AS effective_rank,
                       ss.entry_count, ss.log_weight, ss.max_score
                FROM current_rows
                JOIN song_stats ss ON ss.song_id = current_rows.song_id AND ss.instrument = @instrument
                WHERE ss.entry_count > 0
                  AND COALESCE(NULLIF(current_rows.api_rank, 0), current_rows.rank) > 0
                """;
            c.Parameters.AddWithValue("instrument", Instrument);
            c.ExecuteNonQuery();
        }

        using (var c = conn.CreateCommand())
        {
            c.CommandTimeout = 0;
            c.CommandText = @"
                CREATE INDEX ON _valid_entries (account_id);
                CREATE INDEX ON _valid_entries (max_score, score) WHERE max_score IS NOT NULL AND max_score > 0";
            c.ExecuteNonQuery();
        }

        using (var c = conn.CreateCommand())
        {
            c.CommandText = $"""
                CREATE TEMP TABLE _valid_entries_overrides AS
                WITH current_rows AS (
                    {BuildCurrentStateResolvedEntriesSql()}
                )
                SELECT vso.song_id, vso.account_id, vso.score,
                       COALESCE(vso.accuracy, 0) AS accuracy,
                       COALESCE(vso.is_full_combo, false) AS is_full_combo,
                       COALESCE(vso.stars, 0) AS stars,
                       (
                           SELECT COUNT(*) + 1
                           FROM current_rows current_rows2
                           JOIN song_stats ss2 ON ss2.song_id = current_rows2.song_id AND ss2.instrument = @instrument
                           WHERE current_rows2.song_id = vso.song_id
                             AND current_rows2.score > vso.score
                             AND current_rows2.score <= COALESCE(CAST(ss2.max_score * @threshold AS INTEGER), current_rows2.score + 1)
                             AND current_rows2.account_id != vso.account_id
                       ) AS effective_rank,
                       ss.entry_count, ss.log_weight, ss.max_score
                FROM valid_score_overrides vso
                JOIN current_rows current_member ON current_member.song_id = vso.song_id AND current_member.account_id = vso.account_id
                JOIN song_stats ss ON ss.song_id = vso.song_id AND ss.instrument = vso.instrument
                WHERE vso.instrument = @instrument
                  AND ss.entry_count > 0
                """;
            c.Parameters.AddWithValue("instrument", Instrument);
            c.Parameters.AddWithValue("threshold", baseThreshold);
            c.ExecuteNonQuery();
        }

        using (var c = conn.CreateCommand())
        {
            c.CommandTimeout = 0;
            c.CommandText = "ANALYZE _valid_entries; ANALYZE _valid_entries_overrides";
            c.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Compute account rankings from the materialized <c>_valid_entries</c> temp table.
    /// Must be called on the same connection that created the temp table.
    /// </summary>
    public int ComputeAccountRankingsFromMaterialized(
        NpgsqlConnection conn, int totalChartedSongs,
        int credibilityThreshold, double populationMedian, double thresholdMultiplier)
    {
        using var tx = conn.BeginTransaction();
        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = $"TRUNCATE {GetPartitionName("account_rankings")}";
            c.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = 0;
        cmd.CommandText =
            "WITH ValidEntries AS (" +
            "SELECT song_id, account_id, score, accuracy, is_full_combo, stars, effective_rank, entry_count, log_weight, max_score " +
            "FROM _valid_entries WHERE score <= COALESCE(CAST(max_score * @threshold AS INTEGER), score + 1) " +
            "UNION ALL " +
            "SELECT o.song_id, o.account_id, o.score, o.accuracy, o.is_full_combo, o.stars, " +
            "(SELECT COUNT(*) + 1 FROM _valid_entries le2 " +
            "WHERE le2.song_id = o.song_id AND le2.score > o.score " +
            "AND le2.score <= COALESCE(CAST(le2.max_score * @threshold AS INTEGER), le2.score + 1) AND le2.account_id != o.account_id), " +
            "o.entry_count, o.log_weight, o.max_score " +
            "FROM _valid_entries_overrides o), " +
            "Aggregated AS (" +
            "SELECT v.account_id, COUNT(*) AS songs_played, @totalCharted AS total_charted_songs, CAST(COUNT(*) AS DOUBLE PRECISION) / @totalCharted AS coverage, AVG(CAST(v.effective_rank AS DOUBLE PRECISION) / v.entry_count) AS raw_skill_rating, SUM((CAST(v.effective_rank AS DOUBLE PRECISION) / v.entry_count) * v.log_weight) / NULLIF(SUM(v.log_weight), 0) AS weighted_rating, CAST(SUM(CASE WHEN v.is_full_combo THEN 1 ELSE 0 END) AS DOUBLE PRECISION) / @totalCharted AS fc_rate, SUM(v.score) AS total_score, AVG(CASE WHEN v.max_score IS NOT NULL AND v.max_score > 0 THEN LEAST(CAST(v.score AS DOUBLE PRECISION) / v.max_score, @threshold) ELSE NULL END) AS max_score_percent, AVG(v.accuracy) AS avg_accuracy, SUM(CASE WHEN v.is_full_combo THEN 1 ELSE 0 END) AS full_combo_count, AVG(v.stars) AS avg_stars, MIN(v.effective_rank) AS best_rank, AVG(CAST(v.effective_rank AS DOUBLE PRECISION)) AS avg_rank FROM ValidEntries v GROUP BY v.account_id), " +
            "WithBayesian AS (SELECT *, (songs_played * raw_skill_rating + @m * @C) / (songs_played + @m) AS adjusted_skill_rating, (songs_played * COALESCE(weighted_rating, 1.0) + @m * @C) / (songs_played + @m) AS adjusted_weighted_rating, (songs_played * COALESCE(max_score_percent, 0.5) + @m * @C) / (songs_played + @m) AS adjusted_max_score_percent FROM Aggregated), " +
            "Ranked AS (SELECT *, ROW_NUMBER() OVER (ORDER BY adjusted_skill_rating ASC, songs_played DESC, total_score DESC, full_combo_count DESC, account_id ASC) AS adjusted_skill_rank, ROW_NUMBER() OVER (ORDER BY adjusted_weighted_rating ASC, songs_played DESC, total_score DESC, full_combo_count DESC, account_id ASC) AS weighted_rank, ROW_NUMBER() OVER (ORDER BY fc_rate DESC, total_score DESC, songs_played DESC, adjusted_skill_rating ASC, account_id ASC) AS fc_rate_rank, ROW_NUMBER() OVER (ORDER BY total_score DESC, songs_played DESC, adjusted_skill_rating ASC, account_id ASC) AS total_score_rank, ROW_NUMBER() OVER (ORDER BY adjusted_max_score_percent DESC, songs_played DESC, adjusted_skill_rating ASC, account_id ASC) AS max_score_percent_rank FROM WithBayesian) " +
            "INSERT INTO account_rankings (account_id, instrument, songs_played, total_charted_songs, coverage, raw_skill_rating, adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank, fc_rate, fc_rate_rank, total_score, total_score_rank, max_score_percent, max_score_percent_rank, avg_accuracy, full_combo_count, avg_stars, best_rank, avg_rank, raw_max_score_percent, raw_weighted_rating, computed_at) " +
            "SELECT account_id, @instrument, songs_played, total_charted_songs, coverage, raw_skill_rating, adjusted_skill_rating, adjusted_skill_rank, adjusted_weighted_rating, weighted_rank, fc_rate, fc_rate_rank, total_score, total_score_rank, adjusted_max_score_percent, max_score_percent_rank, avg_accuracy, full_combo_count, avg_stars, best_rank, avg_rank, max_score_percent, weighted_rating, @now FROM Ranked";
        cmd.Parameters.AddWithValue("instrument", Instrument);
        cmd.Parameters.AddWithValue("totalCharted", totalChartedSongs);
        cmd.Parameters.AddWithValue("m", credibilityThreshold);
        cmd.Parameters.AddWithValue("C", populationMedian);
        cmd.Parameters.AddWithValue("threshold", thresholdMultiplier);
        var computedAt = DateTime.UtcNow;
        cmd.Parameters.AddWithValue("now", computedAt);
        int rows = cmd.ExecuteNonQuery();
        UpsertAccountRankingStats(conn, tx, rows, computedAt);
        tx.Commit();
        return rows;
    }

    /// <summary>
    /// Returns accounts with entries in the score band from the materialized temp table.
    /// Must be called on the same connection that created <c>_valid_entries</c>.
    /// </summary>
    public List<(string AccountId, double ActivationLeeway)> GetBandEntriesFromMaterialized(
        NpgsqlConnection conn, double baseThreshold, double maxThreshold)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT account_id,
                ROUND(((score::double precision / max_score) - 1.0) * 1000) / 10.0 AS activation_leeway
            FROM _valid_entries
            WHERE max_score IS NOT NULL AND max_score > 0
              AND score > CAST(max_score * @baseTh AS INTEGER)
              AND score <= CAST(max_score * @maxTh AS INTEGER)";
        cmd.Parameters.AddWithValue("baseTh", baseThreshold);
        cmd.Parameters.AddWithValue("maxTh", maxThreshold);
        var results = new List<(string, double)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            results.Add((r.GetString(0), r.GetDouble(1)));
        return results;
    }

    /// <summary>
    /// Compute aggregate metrics for all leeway buckets in a single SQL pass,
    /// replacing the 101× <see cref="ComputeMetricsAtThreshold"/> C# loop.
    /// Must be called on the same connection that created <c>_valid_entries</c>.
    /// </summary>
    public List<(string AccountId, double LeewayBucket, AccountAggregateMetrics Metrics)> ComputeAllBucketDeltas(
        NpgsqlConnection conn,
        SortedDictionary<double, HashSet<string>> affectedAccountsByBucket,
        HashSet<string> allAffectedAccounts,
        int totalChartedSongs, int credibilityThreshold, double populationMedian)
    {
        if (allAffectedAccounts.Count == 0) return [];

        using (var c = conn.CreateCommand())
        {
            c.CommandText = "DROP TABLE IF EXISTS _bucket_sweep_accounts";
            c.ExecuteNonQuery();
        }

        // Build the cumulative account → first_bucket mapping
        using (var c = conn.CreateCommand())
        {
            c.CommandText = @"
                CREATE TEMP TABLE _bucket_sweep_accounts (
                    account_id TEXT NOT NULL,
                    first_bucket DOUBLE PRECISION NOT NULL
                )";
            c.ExecuteNonQuery();
        }

        // Populate using COPY for efficiency
        using (var writer = conn.BeginBinaryImport(
            "COPY _bucket_sweep_accounts (account_id, first_bucket) FROM STDIN (FORMAT BINARY)"))
        {
            var accountFirstBucket = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (bucket, accounts) in affectedAccountsByBucket)
            {
                foreach (var accountId in accounts)
                {
                    if (!accountFirstBucket.ContainsKey(accountId))
                        accountFirstBucket[accountId] = bucket;
                }
            }

            foreach (var (accountId, firstBucket) in accountFirstBucket)
            {
                writer.StartRow();
                writer.Write(accountId, NpgsqlDbType.Text);
                writer.Write(firstBucket, NpgsqlDbType.Double);
            }
            writer.Complete();
        }

        using (var c = conn.CreateCommand())
        {
            c.CommandText = "CREATE INDEX ON _bucket_sweep_accounts (first_bucket, account_id)";
            c.ExecuteNonQuery();
        }

        // Single SQL pass: generate buckets, join cumulative accounts,
        // filter entries by threshold, aggregate + Bayesian, return all results.
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = @"
            WITH buckets AS (
                SELECT ROUND(generate_series(-49, 50)::numeric / 10, 1) AS bucket
                UNION ALL
                SELECT 99.0
            ),
            bucket_accounts AS (
                SELECT b.bucket, a.account_id
                FROM buckets b
                JOIN _bucket_sweep_accounts a ON a.first_bucket <= b.bucket
            ),
            bucket_entries AS (
                SELECT ba.bucket,
                       v.account_id, v.song_id, v.score, v.accuracy, v.is_full_combo, v.stars,
                       v.effective_rank, v.entry_count, v.log_weight, v.max_score
                FROM bucket_accounts ba
                JOIN _valid_entries v ON v.account_id = ba.account_id
                WHERE ba.bucket = 99.0
                   OR v.score <= COALESCE(CAST(v.max_score * (1.0 + ba.bucket / 100.0) AS INTEGER), v.score + 1)
                UNION ALL
                SELECT ba.bucket,
                       o.account_id, o.song_id, o.score, o.accuracy, o.is_full_combo, o.stars,
                       (SELECT COUNT(*) + 1 FROM _valid_entries le2
                        WHERE le2.song_id = o.song_id AND le2.score > o.score
                        AND (ba.bucket = 99.0 OR le2.score <= COALESCE(CAST(le2.max_score * (1.0 + ba.bucket / 100.0) AS INTEGER), le2.score + 1))
                        AND le2.account_id != o.account_id) AS effective_rank,
                       o.entry_count, o.log_weight, o.max_score
                FROM bucket_accounts ba
                JOIN _valid_entries_overrides o ON o.account_id = ba.account_id
            ),
            aggregated AS (
                SELECT bucket, account_id,
                    COUNT(*) AS songs_played,
                    CAST(COUNT(*) AS DOUBLE PRECISION) / @totalCharted AS coverage,
                    AVG(CAST(effective_rank AS DOUBLE PRECISION) / entry_count) AS raw_skill,
                    SUM((CAST(effective_rank AS DOUBLE PRECISION) / entry_count) * log_weight)
                        / NULLIF(SUM(log_weight), 0) AS weighted,
                    CAST(SUM(CASE WHEN is_full_combo THEN 1 ELSE 0 END) AS DOUBLE PRECISION)
                        / @totalCharted AS fc_rate,
                    SUM(score) AS total_score,
                    AVG(CASE WHEN max_score IS NOT NULL AND max_score > 0
                        THEN LEAST(CAST(score AS DOUBLE PRECISION) / max_score,
                             CASE WHEN bucket = 99.0 THEN 99.0
                                  ELSE 1.0 + bucket / 100.0 END)
                        ELSE NULL END) AS max_score_pct,
                    AVG(accuracy) AS avg_accuracy,
                    SUM(CASE WHEN is_full_combo THEN 1 ELSE 0 END) AS full_combo_count,
                    MIN(effective_rank) AS best_rank
                FROM bucket_entries
                GROUP BY bucket, account_id
            )
            SELECT bucket, account_id, songs_played, coverage,
                (songs_played * raw_skill + @m * @C) / (songs_played + @m) AS adjusted_skill,
                (songs_played * COALESCE(weighted, 1.0) + @m * @C) / (songs_played + @m) AS adj_weighted,
                fc_rate, total_score,
                (songs_played * COALESCE(max_score_pct, 0.5) + @m * @C) / (songs_played + @m) AS adj_max_score_pct,
                avg_accuracy, full_combo_count, best_rank
            FROM aggregated
            ORDER BY bucket, account_id";
        cmd.Parameters.AddWithValue("totalCharted", totalChartedSongs);
        cmd.Parameters.AddWithValue("m", credibilityThreshold);
        cmd.Parameters.AddWithValue("C", populationMedian);

        var results = new List<(string, double, AccountAggregateMetrics)>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                results.Add((r.GetString(1), r.GetDouble(0), new AccountAggregateMetrics
                {
                    SongsPlayed = r.GetInt32(2),
                    Coverage = r.GetDouble(3),
                    AdjustedSkill = r.GetDouble(4),
                    Weighted = r.GetDouble(5),
                    FcRate = r.GetDouble(6),
                    TotalScore = r.GetInt64(7),
                    MaxScorePct = r.GetDouble(8),
                    AvgAccuracy = r.GetDouble(9),
                    FullComboCount = r.GetInt32(10),
                    BestRank = r.GetInt32(11),
                }));
            }
        }

        // Cleanup
        using (var c = conn.CreateCommand())
        {
            c.CommandText = "DROP TABLE IF EXISTS _bucket_sweep_accounts";
            c.ExecuteNonQuery();
        }

        return results;
    }

    /// <summary>
    /// Write ranking deltas using COPY binary for maximum throughput.
    /// </summary>
    public void WriteRankingDeltasBulk(IReadOnlyList<(string AccountId, double LeewayBucket,
        int SongsPlayed, double AdjustedSkill, double Weighted, double FcRate, long TotalScore,
        double MaxScorePct, int FullComboCount, double AvgAccuracy, int BestRank, double Coverage)> deltas)
    {
        if (deltas.Count == 0) return;
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();

        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = @"
                CREATE TEMP TABLE _rd_staging (
                    account_id TEXT, instrument TEXT, leeway_bucket REAL,
                    songs_played INTEGER, adjusted_skill DOUBLE PRECISION, weighted DOUBLE PRECISION,
                    fc_rate DOUBLE PRECISION, total_score BIGINT, max_score_pct DOUBLE PRECISION,
                    full_combo_count INTEGER, avg_accuracy DOUBLE PRECISION, best_rank INTEGER,
                    coverage DOUBLE PRECISION
                ) ON COMMIT DROP";
            c.ExecuteNonQuery();
        }

        using (var writer = conn.BeginBinaryImport(
            "COPY _rd_staging (account_id, instrument, leeway_bucket, songs_played, adjusted_skill, " +
            "weighted, fc_rate, total_score, max_score_pct, full_combo_count, avg_accuracy, best_rank, coverage) " +
            "FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var d in deltas)
            {
                writer.StartRow();
                writer.Write(d.AccountId, NpgsqlDbType.Text);
                writer.Write(Instrument, NpgsqlDbType.Text);
                writer.Write((float)d.LeewayBucket, NpgsqlDbType.Real);
                writer.Write(d.SongsPlayed, NpgsqlDbType.Integer);
                writer.Write(d.AdjustedSkill, NpgsqlDbType.Double);
                writer.Write(d.Weighted, NpgsqlDbType.Double);
                writer.Write(d.FcRate, NpgsqlDbType.Double);
                writer.Write(d.TotalScore, NpgsqlDbType.Bigint);
                writer.Write(d.MaxScorePct, NpgsqlDbType.Double);
                writer.Write(d.FullComboCount, NpgsqlDbType.Integer);
                writer.Write(d.AvgAccuracy, NpgsqlDbType.Double);
                writer.Write(d.BestRank, NpgsqlDbType.Integer);
                writer.Write(d.Coverage, NpgsqlDbType.Double);
            }
            writer.Complete();
        }

        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = "INSERT INTO ranking_deltas SELECT * FROM _rd_staging";
            c.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// Opens and returns a raw connection for use by the ranking pipeline.
    /// The caller is responsible for disposing the connection.
    /// </summary>
    public NpgsqlConnection OpenConnection() => _ds.OpenConnection();

    public void Dispose() { }
}
