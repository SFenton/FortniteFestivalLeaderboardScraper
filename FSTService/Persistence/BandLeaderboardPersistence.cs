using FSTService.Scraping;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Persistence;

/// <summary>
/// Persistence layer for band leaderboard data (Duets, Trios, Quads).
/// Uses the same COPY-based bulk upsert pattern as <see cref="InstrumentDatabase"/>
/// to write into the <c>band_entries</c>, <c>band_member_stats</c>, and <c>band_members</c> tables.
/// </summary>
public sealed class BandLeaderboardPersistence
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<BandLeaderboardPersistence> _log;

    public BandLeaderboardPersistence(NpgsqlDataSource dataSource, ILogger<BandLeaderboardPersistence> log)
    {
        _dataSource = dataSource;
        _log = log;
    }

    /// <summary>
    /// Upsert a batch of band leaderboard entries for one (song, bandType).
    /// Also persists per-member stats and the band_members lookup rows.
    /// </summary>
    public int UpsertBandEntries(string songId, string bandType, IReadOnlyList<BandLeaderboardEntry> entries)
    {
        if (entries.Count == 0)
            return 0;

        var now = DateTimeOffset.UtcNow;
        using var conn = _dataSource.OpenConnection();
        using var tx = conn.BeginTransaction();

        try
        {
            // ── 1. COPY band entries into staging ──
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    CREATE TEMP TABLE _be_staging (
                        song_id TEXT, band_type TEXT, team_key TEXT, team_members TEXT[],
                        score INT, base_score INT, instrument_bonus INT, overdrive_bonus INT,
                        accuracy INT, is_full_combo BOOLEAN, stars INT, difficulty INT,
                        season INT, rank INT, percentile DOUBLE PRECISION, end_time TEXT,
                        source TEXT, is_over_threshold BOOLEAN, ts TIMESTAMPTZ
                    ) ON COMMIT DROP
                    """;
                cmd.ExecuteNonQuery();
            }

            using (var writer = conn.BeginBinaryImport(
                "COPY _be_staging (song_id, band_type, team_key, team_members, score, base_score, " +
                "instrument_bonus, overdrive_bonus, accuracy, is_full_combo, stars, difficulty, " +
                "season, rank, percentile, end_time, source, is_over_threshold, ts) FROM STDIN (FORMAT BINARY)"))
            {
                foreach (var e in entries)
                {
                    writer.StartRow();
                    writer.Write(songId, NpgsqlDbType.Text);
                    writer.Write(bandType, NpgsqlDbType.Text);
                    writer.Write(e.TeamKey, NpgsqlDbType.Text);
                    writer.Write(e.TeamMembers, NpgsqlDbType.Array | NpgsqlDbType.Text);
                    writer.Write(e.Score, NpgsqlDbType.Integer);
                    WriteNullableInt(writer, e.BaseScore);
                    WriteNullableInt(writer, e.InstrumentBonus);
                    WriteNullableInt(writer, e.OverdriveBonus);
                    writer.Write(e.Accuracy, NpgsqlDbType.Integer);
                    writer.Write(e.IsFullCombo, NpgsqlDbType.Boolean);
                    writer.Write(e.Stars, NpgsqlDbType.Integer);
                    writer.Write(e.Difficulty, NpgsqlDbType.Integer);
                    writer.Write(e.Season, NpgsqlDbType.Integer);
                    writer.Write(e.Rank, NpgsqlDbType.Integer);
                    writer.Write(e.Percentile, NpgsqlDbType.Double);
                    if (e.EndTime is not null) writer.Write(e.EndTime, NpgsqlDbType.Text);
                    else writer.WriteNull();
                    writer.Write(e.Source ?? "scrape", NpgsqlDbType.Text);
                    writer.Write(e.IsOverThreshold, NpgsqlDbType.Boolean);
                    writer.Write(now, NpgsqlDbType.TimestampTz);
                }
                writer.Complete();
            }

            // ── 2. Merge staging into band_entries ──
            int merged;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO band_entries (song_id, band_type, team_key, team_members, score,
                        base_score, instrument_bonus, overdrive_bonus, accuracy, is_full_combo,
                        stars, difficulty, season, rank, percentile, end_time, source,
                        is_over_threshold, first_seen_at, last_updated_at)
                    SELECT DISTINCT ON (song_id, band_type, team_key)
                        song_id, band_type, team_key, team_members, score,
                        base_score, instrument_bonus, overdrive_bonus, accuracy, is_full_combo,
                        stars, difficulty, season, rank, percentile, end_time, source,
                        is_over_threshold, ts, ts
                    FROM _be_staging
                    ORDER BY song_id, band_type, team_key, score DESC
                    ON CONFLICT (song_id, band_type, team_key) DO UPDATE SET
                        score = CASE WHEN EXCLUDED.score > band_entries.score THEN EXCLUDED.score ELSE band_entries.score END,
                        base_score = COALESCE(EXCLUDED.base_score, band_entries.base_score),
                        instrument_bonus = COALESCE(EXCLUDED.instrument_bonus, band_entries.instrument_bonus),
                        overdrive_bonus = COALESCE(EXCLUDED.overdrive_bonus, band_entries.overdrive_bonus),
                        accuracy = CASE WHEN EXCLUDED.score > band_entries.score THEN EXCLUDED.accuracy ELSE band_entries.accuracy END,
                        is_full_combo = CASE WHEN EXCLUDED.score > band_entries.score THEN EXCLUDED.is_full_combo ELSE band_entries.is_full_combo END,
                        stars = CASE WHEN EXCLUDED.score > band_entries.score THEN EXCLUDED.stars ELSE band_entries.stars END,
                        difficulty = CASE WHEN EXCLUDED.score > band_entries.score THEN EXCLUDED.difficulty ELSE band_entries.difficulty END,
                        season = CASE WHEN EXCLUDED.score > band_entries.score THEN EXCLUDED.season ELSE band_entries.season END,
                        rank = CASE WHEN EXCLUDED.score > band_entries.score THEN EXCLUDED.rank ELSE band_entries.rank END,
                        percentile = CASE WHEN EXCLUDED.score > band_entries.score THEN EXCLUDED.percentile ELSE band_entries.percentile END,
                        end_time = CASE WHEN EXCLUDED.score > band_entries.score THEN EXCLUDED.end_time ELSE band_entries.end_time END,
                        is_over_threshold = CASE WHEN EXCLUDED.score > band_entries.score THEN EXCLUDED.is_over_threshold ELSE band_entries.is_over_threshold END,
                        last_updated_at = CASE WHEN EXCLUDED.score > band_entries.score THEN EXCLUDED.last_updated_at ELSE band_entries.last_updated_at END
                    """;
                merged = cmd.ExecuteNonQuery();
            }

            // ── 3. COPY band_member_stats ──
            var allMemberStats = entries
                .Where(e => e.MemberStats.Count > 0)
                .SelectMany(e => e.MemberStats.Select(ms => (e.TeamKey, ms)))
                .ToList();

            if (allMemberStats.Count > 0)
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        CREATE TEMP TABLE _bms_staging (
                            song_id TEXT, band_type TEXT, team_key TEXT,
                            member_index INT, account_id TEXT, instrument_id INT,
                            score INT, accuracy INT, is_full_combo BOOLEAN,
                            stars INT, difficulty INT
                        ) ON COMMIT DROP
                        """;
                    cmd.ExecuteNonQuery();
                }

                using (var writer = conn.BeginBinaryImport(
                    "COPY _bms_staging (song_id, band_type, team_key, member_index, account_id, " +
                    "instrument_id, score, accuracy, is_full_combo, stars, difficulty) FROM STDIN (FORMAT BINARY)"))
                {
                    foreach (var (teamKey, ms) in allMemberStats)
                    {
                        writer.StartRow();
                        writer.Write(songId, NpgsqlDbType.Text);
                        writer.Write(bandType, NpgsqlDbType.Text);
                        writer.Write(teamKey, NpgsqlDbType.Text);
                        writer.Write(ms.MemberIndex, NpgsqlDbType.Integer);
                        writer.Write(ms.AccountId, NpgsqlDbType.Text);
                        writer.Write(ms.InstrumentId, NpgsqlDbType.Integer);
                        writer.Write(ms.Score, NpgsqlDbType.Integer);
                        writer.Write(ms.Accuracy, NpgsqlDbType.Integer);
                        writer.Write(ms.IsFullCombo, NpgsqlDbType.Boolean);
                        writer.Write(ms.Stars, NpgsqlDbType.Integer);
                        writer.Write(ms.Difficulty, NpgsqlDbType.Integer);
                    }
                    writer.Complete();
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT INTO band_member_stats (song_id, band_type, team_key, member_index,
                            account_id, instrument_id, score, accuracy, is_full_combo, stars, difficulty)
                        SELECT song_id, band_type, team_key, member_index,
                            account_id, instrument_id, score, accuracy, is_full_combo, stars, difficulty
                        FROM _bms_staging
                        ON CONFLICT (song_id, band_type, team_key, member_index) DO UPDATE SET
                            account_id = EXCLUDED.account_id,
                            instrument_id = EXCLUDED.instrument_id,
                            score = EXCLUDED.score,
                            accuracy = EXCLUDED.accuracy,
                            is_full_combo = EXCLUDED.is_full_combo,
                            stars = EXCLUDED.stars,
                            difficulty = EXCLUDED.difficulty
                        """;
                    cmd.ExecuteNonQuery();
                }
            }

            // ── 4. COPY band_members (denormalized lookup) ──
            var memberLookups = entries
                .SelectMany(e => e.TeamMembers.Select(m => (AccountId: m, e.TeamKey)))
                .Where(x => !string.IsNullOrEmpty(x.AccountId))
                .Distinct()
                .ToList();

            if (memberLookups.Count > 0)
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        CREATE TEMP TABLE _bm_staging (
                            account_id TEXT, song_id TEXT, band_type TEXT, team_key TEXT
                        ) ON COMMIT DROP
                        """;
                    cmd.ExecuteNonQuery();
                }

                using (var writer = conn.BeginBinaryImport(
                    "COPY _bm_staging (account_id, song_id, band_type, team_key) FROM STDIN (FORMAT BINARY)"))
                {
                    foreach (var (accountId, teamKey) in memberLookups)
                    {
                        writer.StartRow();
                        writer.Write(accountId, NpgsqlDbType.Text);
                        writer.Write(songId, NpgsqlDbType.Text);
                        writer.Write(bandType, NpgsqlDbType.Text);
                        writer.Write(teamKey, NpgsqlDbType.Text);
                    }
                    writer.Complete();
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT INTO band_members (account_id, song_id, band_type, team_key)
                        SELECT account_id, song_id, band_type, team_key FROM _bm_staging
                        ON CONFLICT (account_id, song_id, band_type, team_key) DO NOTHING
                        """;
                    cmd.ExecuteNonQuery();
                }
            }

            tx.Commit();
            return merged;
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }
    }

    private static void WriteNullableInt(NpgsqlBinaryImporter writer, int? value)
    {
        if (value.HasValue) writer.Write(value.Value, NpgsqlDbType.Integer);
        else writer.WriteNull();
    }
}
