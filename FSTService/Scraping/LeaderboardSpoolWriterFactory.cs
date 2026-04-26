using System.Buffers.Binary;
using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// Solo leaderboard spool — thin wrapper around <see cref="SpoolWriter{T}"/>
/// providing <see cref="LeaderboardEntry"/>-specific binary serialization
/// and a flush delegate that batch-upserts via <see cref="InstrumentDatabase"/>.
/// </summary>
public static class LeaderboardSpoolWriterFactory
{
    public static SpoolWriter<LeaderboardEntry> Create(ILogger log, GlobalLeaderboardPersistence persistence, long scrapeId, string? baseDirectory = null)
    {
        return new SpoolWriter<LeaderboardEntry>(
            log, "solo",
            serialize: SerializeSoloPage,
            deserialize: DeserializeSoloPage,
            flush: (instrument, batch) => FlushSoloBatch(log, persistence, scrapeId, instrument, batch),
            baseDirectory: baseDirectory);
    }

    private static void FlushSoloBatch(ILogger log, GlobalLeaderboardPersistence persistence, long scrapeId, string instrument,
                                        List<(string SongId, IReadOnlyList<LeaderboardEntry> Entries)> batch)
    {
        var db = (InstrumentDatabase)persistence.GetOrCreateInstrumentDb(instrument);
        var activeInstrument = db.Instrument;
        var writeLegacyLiveRows = persistence.WriteLegacyLiveLeaderboardDuringScrape;

        try
        {
            using var conn = db.DataSource.OpenConnection();
            using var tx = conn.BeginTransaction();
            using (var sc = conn.CreateCommand())
            {
                sc.Transaction = tx;
                sc.CommandText = "SET LOCAL synchronous_commit = off";
                sc.ExecuteNonQuery();
            }

            // Single COPY + merge for all songs in the batch — avoids per-song
            // DROP/CREATE/COPY/MERGE/DROP staging table cycles.
            var now = System.DateTime.UtcNow;

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DROP TABLE IF EXISTS _le_staging";
                cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    "CREATE TEMP TABLE _le_staging (" +
                    "song_id TEXT, instrument TEXT, account_id TEXT, score INTEGER, accuracy INTEGER, " +
                    "is_full_combo BOOLEAN, stars INTEGER, season INTEGER, difficulty INTEGER, " +
                    "percentile DOUBLE PRECISION, rank INTEGER, end_time TEXT, api_rank INTEGER, " +
                    "source TEXT, band_members_json JSONB, band_score INTEGER, base_score INTEGER, " +
                    "instrument_bonus INTEGER, overdrive_bonus INTEGER, instrument_combo TEXT, ts TIMESTAMPTZ" +
                    ")";
                cmd.ExecuteNonQuery();
            }

            using (var writer = conn.BeginBinaryImport(
                "COPY _le_staging (song_id, instrument, account_id, score, accuracy, is_full_combo, " +
                "stars, season, difficulty, percentile, rank, end_time, api_rank, source, " +
                "band_members_json, band_score, base_score, instrument_bonus, overdrive_bonus, instrument_combo, ts) FROM STDIN (FORMAT BINARY)"))
            {
                foreach (var (songId, entries) in batch)
                {
                    foreach (var e in entries)
                    {
                        writer.StartRow();
                        writer.Write(songId, NpgsqlTypes.NpgsqlDbType.Text);
                        writer.Write(activeInstrument, NpgsqlTypes.NpgsqlDbType.Text);
                        writer.Write(e.AccountId, NpgsqlTypes.NpgsqlDbType.Text);
                        writer.Write(e.Score, NpgsqlTypes.NpgsqlDbType.Integer);
                        writer.Write(e.Accuracy, NpgsqlTypes.NpgsqlDbType.Integer);
                        writer.Write(e.IsFullCombo, NpgsqlTypes.NpgsqlDbType.Boolean);
                        writer.Write(e.Stars, NpgsqlTypes.NpgsqlDbType.Integer);
                        writer.Write(e.Season, NpgsqlTypes.NpgsqlDbType.Integer);
                        writer.Write(e.Difficulty, NpgsqlTypes.NpgsqlDbType.Integer);
                        writer.Write(e.Percentile, NpgsqlTypes.NpgsqlDbType.Double);
                        writer.Write(e.Rank, NpgsqlTypes.NpgsqlDbType.Integer);
                        if (e.EndTime is not null) writer.Write(e.EndTime, NpgsqlTypes.NpgsqlDbType.Text);
                        else writer.WriteNull();
                        if (e.ApiRank > 0) writer.Write(e.ApiRank, NpgsqlTypes.NpgsqlDbType.Integer);
                        else writer.WriteNull();
                        writer.Write(e.Source ?? "scrape", NpgsqlTypes.NpgsqlDbType.Text);
                        var bandJson = SerializeBandMembers(e);
                        if (bandJson is not null) writer.Write(bandJson, NpgsqlTypes.NpgsqlDbType.Jsonb);
                        else writer.WriteNull();
                        if (e.BandScore.HasValue) writer.Write(e.BandScore.Value, NpgsqlTypes.NpgsqlDbType.Integer);
                        else writer.WriteNull();
                        if (e.BaseScore.HasValue) writer.Write(e.BaseScore.Value, NpgsqlTypes.NpgsqlDbType.Integer);
                        else writer.WriteNull();
                        if (e.InstrumentBonus.HasValue) writer.Write(e.InstrumentBonus.Value, NpgsqlTypes.NpgsqlDbType.Integer);
                        else writer.WriteNull();
                        if (e.OverdriveBonus.HasValue) writer.Write(e.OverdriveBonus.Value, NpgsqlTypes.NpgsqlDbType.Integer);
                        else writer.WriteNull();
                        if (e.InstrumentCombo is not null) writer.Write(e.InstrumentCombo, NpgsqlTypes.NpgsqlDbType.Text);
                        else writer.WriteNull();
                        writer.Write(now, NpgsqlTypes.NpgsqlDbType.TimestampTz);
                    }
                }
                writer.Complete();
            }

            if (scrapeId > 0)
            {
                using var snapshotCmd = conn.CreateCommand();
                snapshotCmd.Transaction = tx;
                snapshotCmd.CommandTimeout = 0;
                snapshotCmd.CommandText = BuildSnapshotInsertSql();
                snapshotCmd.Parameters.AddWithValue("snapshotId", scrapeId);
                snapshotCmd.Parameters.AddWithValue("instrument", activeInstrument);
                snapshotCmd.ExecuteNonQuery();
            }

            if (writeLegacyLiveRows)
            {
                // ── Statement 1: Score-gated ON CONFLICT ──
                // Inserts new entries and updates existing ones ONLY when score changed
                // or one-time fill-in fields need populating. Skips the full 20-column
                // tuple rewrite for the ~95% of rows where only api_rank shifted.
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandTimeout = 0; // Unlimited — bulk merge of millions of rows
                    cmd.CommandText = BuildScoreMergeSql();
                    cmd.Parameters.AddWithValue("instrument", activeInstrument);
                    cmd.ExecuteNonQuery();
                }

                // ── Statement 2: Lightweight api_rank + rank UPDATE ──
                // For existing entries where only the rank shifted (no score change),
                // update just the rank columns. This writes a much smaller tuple + WAL
                // than the full 20-column ON CONFLICT rewrite above.
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandTimeout = 0;
                    cmd.CommandText = BuildRankUpdateSql();
                    cmd.Parameters.AddWithValue("instrument", activeInstrument);
                    cmd.ExecuteNonQuery();
                }
            }
            else
            {
                log.LogDebug("Skipped legacy live leaderboard_entries merge for {Instrument} ({Songs} songs, {Entries:N0} entries); snapshot rows remain authoritative for scrape {ScrapeId}.",
                    activeInstrument, batch.Count, batch.Sum(b => b.Entries.Count), scrapeId);
            }

            using (var cmd = conn.CreateCommand()) { cmd.Transaction = tx; cmd.CommandText = "DROP TABLE IF EXISTS _le_staging"; cmd.ExecuteNonQuery(); }

            tx.Commit();
        }
        catch (Npgsql.PostgresException pex) when (pex.SqlState == "23514" || (pex.MessageText?.Contains("no partition", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            log.LogCritical(pex,
                "*** SCHEMA MISMATCH *** Spool [solo] flush FAILED for instrument {Instrument}: no Postgres partition exists for this value. " +
                "Dropped {Songs} songs / {Entries:N0} entries. This will recur every scrape until DatabaseInitializer partitions are added. " +
                "SqlState={SqlState} Message={Message}",
                instrument, batch.Count, batch.Sum(b => b.Entries.Count), pex.SqlState, pex.MessageText);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Spool [solo] flush failed for {Instrument} ({Songs} songs, {Entries:N0} entries). Data will be re-scraped next pass.",
                instrument, batch.Count, batch.Sum(b => b.Entries.Count));
        }
    }

    internal static string BuildSnapshotInsertSql() =>
        "INSERT INTO leaderboard_entries_snapshot (snapshot_id, song_id, instrument, account_id, score, accuracy, is_full_combo, stars, season, percentile, rank, source, difficulty, api_rank, end_time, band_members_json, band_score, base_score, instrument_bonus, overdrive_bonus, instrument_combo, first_seen_at, last_updated_at) " +
        "SELECT @snapshotId, song_id, instrument, account_id, score, accuracy, is_full_combo, stars, season, percentile, rank, source, difficulty, api_rank, end_time, band_members_json, band_score, base_score, instrument_bonus, overdrive_bonus, instrument_combo, ts, ts " +
        "FROM (SELECT DISTINCT ON (song_id, instrument, account_id) song_id, instrument, account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, rank, source, api_rank, end_time, band_members_json, band_score, base_score, instrument_bonus, overdrive_bonus, instrument_combo, ts FROM _le_staging WHERE instrument = @instrument ORDER BY song_id, instrument, account_id, score DESC, ts DESC) snapshot_rows " +
        "ON CONFLICT (snapshot_id, song_id, instrument, account_id) DO UPDATE SET " +
        "score = EXCLUDED.score, accuracy = EXCLUDED.accuracy, is_full_combo = EXCLUDED.is_full_combo, stars = EXCLUDED.stars, season = EXCLUDED.season, percentile = EXCLUDED.percentile, rank = EXCLUDED.rank, source = EXCLUDED.source, difficulty = EXCLUDED.difficulty, api_rank = EXCLUDED.api_rank, end_time = EXCLUDED.end_time, band_members_json = EXCLUDED.band_members_json, band_score = EXCLUDED.band_score, base_score = EXCLUDED.base_score, instrument_bonus = EXCLUDED.instrument_bonus, overdrive_bonus = EXCLUDED.overdrive_bonus, instrument_combo = EXCLUDED.instrument_combo, last_updated_at = EXCLUDED.last_updated_at";

    internal static string BuildScoreMergeSql() =>
        "INSERT INTO leaderboard_entries (song_id, instrument, account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, rank, end_time, api_rank, source, band_members_json, band_score, base_score, instrument_bonus, overdrive_bonus, instrument_combo, first_seen_at, last_updated_at) " +
        "SELECT DISTINCT ON (song_id, instrument, account_id) song_id, instrument, account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, rank, end_time, api_rank, source, band_members_json, band_score, base_score, instrument_bonus, overdrive_bonus, instrument_combo, ts, ts FROM _le_staging WHERE instrument = @instrument " +
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
        "last_updated_at = EXCLUDED.last_updated_at " +
        "WHERE leaderboard_entries.instrument = @instrument AND (" +
        "EXCLUDED.score != leaderboard_entries.score " +
        "OR (EXCLUDED.source = 'scrape' AND leaderboard_entries.source != 'scrape') " +
        "OR (EXCLUDED.difficulty >= 0 AND leaderboard_entries.difficulty < 0) " +
        "OR (EXCLUDED.percentile > 0 AND leaderboard_entries.percentile <= 0) " +
        "OR (EXCLUDED.band_members_json IS NOT NULL AND leaderboard_entries.band_members_json IS NULL) " +
        "OR COALESCE(EXCLUDED.base_score, -1) != COALESCE(leaderboard_entries.base_score, -1) " +
        "OR COALESCE(EXCLUDED.overdrive_bonus, -1) != COALESCE(leaderboard_entries.overdrive_bonus, -1))";

    internal static string BuildRankUpdateSql() =>
        "UPDATE leaderboard_entries le " +
        "SET api_rank = s.api_rank, rank = s.rank, last_updated_at = s.ts " +
        "FROM (SELECT DISTINCT ON (song_id, instrument, account_id) " +
        "song_id, instrument, account_id, api_rank, rank, ts " +
        "FROM _le_staging WHERE instrument = @instrument ORDER BY song_id, instrument, account_id, score DESC) s " +
        "WHERE le.instrument = @instrument AND le.song_id = s.song_id AND le.instrument = s.instrument AND le.account_id = s.account_id " +
        "AND (le.api_rank IS DISTINCT FROM s.api_rank OR (s.rank > 0 AND le.rank IS DISTINCT FROM s.rank))";

    private static string? SerializeBandMembers(LeaderboardEntry e)
    {
        if (e.BandMembers is not { Count: >= 2 }) return null;
        return System.Text.Json.JsonSerializer.Serialize(e.BandMembers,
            Persistence.BandMembersJsonContext.Default.ListBandMemberStats);
    }

    // ── Solo serialization ──────────────────────────────────────

    private static void SerializeSoloPage(MemoryStream buf, byte[] header, string songId, IReadOnlyList<LeaderboardEntry> entries)
    {
        SpoolWriter<LeaderboardEntry>.WriteString(buf, header, songId);
        BinaryPrimitives.WriteInt32LittleEndian(header, entries.Count);
        buf.Write(header, 0, 4);

        foreach (var e in entries)
            SerializeEntry(buf, header, e);
    }

    private static (string SongId, IReadOnlyList<LeaderboardEntry> Entries) DeserializeSoloPage(Stream stream, byte[] header)
    {
        var songId = SpoolWriter<LeaderboardEntry>.ReadString(stream, header);
        SpoolWriter<LeaderboardEntry>.ReadExact(stream, header.AsSpan(0, 4));
        int count = BinaryPrimitives.ReadInt32LittleEndian(header);

        var entries = new LeaderboardEntry[count];
        for (int i = 0; i < count; i++)
            entries[i] = DeserializeEntry(stream, header);
        return (songId, entries);
    }

    private static void SerializeEntry(MemoryStream buf, byte[] header, LeaderboardEntry e)
    {
        SpoolWriter<LeaderboardEntry>.WriteString(buf, header, e.AccountId);
        SpoolWriter<LeaderboardEntry>.WriteInt32(buf, header, e.Score);
        SpoolWriter<LeaderboardEntry>.WriteInt32(buf, header, e.Accuracy);
        buf.WriteByte(e.IsFullCombo ? (byte)1 : (byte)0);
        SpoolWriter<LeaderboardEntry>.WriteInt32(buf, header, e.Stars);
        SpoolWriter<LeaderboardEntry>.WriteInt32(buf, header, e.Season);
        SpoolWriter<LeaderboardEntry>.WriteInt32(buf, header, e.Difficulty);
        BinaryPrimitives.WriteDoubleLittleEndian(header, e.Percentile);
        buf.Write(header, 0, 8);
        SpoolWriter<LeaderboardEntry>.WriteInt32(buf, header, e.Rank);
        SpoolWriter<LeaderboardEntry>.WriteInt32(buf, header, e.ApiRank);
        SpoolWriter<LeaderboardEntry>.WriteNullableString(buf, header, e.EndTime);
        SpoolWriter<LeaderboardEntry>.WriteNullableString(buf, header, e.Source);

        if (e.BandMembers is { Count: >= 2 })
        {
            SpoolWriter<LeaderboardEntry>.WriteInt32(buf, header, e.BandMembers.Count);
            foreach (var m in e.BandMembers)
            {
                SpoolWriter<LeaderboardEntry>.WriteInt32(buf, header, m.MemberIndex);
                SpoolWriter<LeaderboardEntry>.WriteString(buf, header, m.AccountId);
                SpoolWriter<LeaderboardEntry>.WriteInt32(buf, header, m.InstrumentId);
                SpoolWriter<LeaderboardEntry>.WriteInt32(buf, header, m.Score);
                SpoolWriter<LeaderboardEntry>.WriteInt32(buf, header, m.Accuracy);
                buf.WriteByte(m.IsFullCombo ? (byte)1 : (byte)0);
                SpoolWriter<LeaderboardEntry>.WriteInt32(buf, header, m.Stars);
                SpoolWriter<LeaderboardEntry>.WriteInt32(buf, header, m.Difficulty);
            }
        }
        else
        {
            SpoolWriter<LeaderboardEntry>.WriteInt32(buf, header, 0);
        }

        SpoolWriter<LeaderboardEntry>.WriteNullableInt32(buf, header, e.BandScore);
        SpoolWriter<LeaderboardEntry>.WriteNullableInt32(buf, header, e.BaseScore);
        SpoolWriter<LeaderboardEntry>.WriteNullableInt32(buf, header, e.InstrumentBonus);
        SpoolWriter<LeaderboardEntry>.WriteNullableInt32(buf, header, e.OverdriveBonus);
        SpoolWriter<LeaderboardEntry>.WriteNullableString(buf, header, e.InstrumentCombo);
    }

    private static LeaderboardEntry DeserializeEntry(Stream stream, byte[] header)
    {
        var entry = new LeaderboardEntry
        {
            AccountId = SpoolWriter<LeaderboardEntry>.ReadString(stream, header),
            Score = SpoolWriter<LeaderboardEntry>.ReadInt32(stream, header),
            Accuracy = SpoolWriter<LeaderboardEntry>.ReadInt32(stream, header),
        };

        SpoolWriter<LeaderboardEntry>.ReadExact(stream, header.AsSpan(0, 1));
        entry.IsFullCombo = header[0] == 1;

        entry.Stars = SpoolWriter<LeaderboardEntry>.ReadInt32(stream, header);
        entry.Season = SpoolWriter<LeaderboardEntry>.ReadInt32(stream, header);
        entry.Difficulty = SpoolWriter<LeaderboardEntry>.ReadInt32(stream, header);
        entry.Percentile = SpoolWriter<LeaderboardEntry>.ReadDouble(stream, header);
        entry.Rank = SpoolWriter<LeaderboardEntry>.ReadInt32(stream, header);
        entry.ApiRank = SpoolWriter<LeaderboardEntry>.ReadInt32(stream, header);
        entry.EndTime = SpoolWriter<LeaderboardEntry>.ReadNullableString(stream, header);
        entry.Source = SpoolWriter<LeaderboardEntry>.ReadNullableString(stream, header) ?? "scrape";

        int bandCount = SpoolWriter<LeaderboardEntry>.ReadInt32(stream, header);
        if (bandCount > 0)
        {
            entry.BandMembers = new List<BandMemberStats>(bandCount);
            for (int i = 0; i < bandCount; i++)
            {
                var m = new BandMemberStats
                {
                    MemberIndex = SpoolWriter<LeaderboardEntry>.ReadInt32(stream, header),
                    AccountId = SpoolWriter<LeaderboardEntry>.ReadString(stream, header),
                    InstrumentId = SpoolWriter<LeaderboardEntry>.ReadInt32(stream, header),
                    Score = SpoolWriter<LeaderboardEntry>.ReadInt32(stream, header),
                    Accuracy = SpoolWriter<LeaderboardEntry>.ReadInt32(stream, header),
                };
                SpoolWriter<LeaderboardEntry>.ReadExact(stream, header.AsSpan(0, 1));
                m.IsFullCombo = header[0] == 1;
                m.Stars = SpoolWriter<LeaderboardEntry>.ReadInt32(stream, header);
                m.Difficulty = SpoolWriter<LeaderboardEntry>.ReadInt32(stream, header);
                entry.BandMembers.Add(m);
            }
        }

        entry.BandScore = SpoolWriter<LeaderboardEntry>.ReadNullableInt32(stream, header);
        entry.BaseScore = SpoolWriter<LeaderboardEntry>.ReadNullableInt32(stream, header);
        entry.InstrumentBonus = SpoolWriter<LeaderboardEntry>.ReadNullableInt32(stream, header);
        entry.OverdriveBonus = SpoolWriter<LeaderboardEntry>.ReadNullableInt32(stream, header);
        entry.InstrumentCombo = SpoolWriter<LeaderboardEntry>.ReadNullableString(stream, header);

        return entry;
    }
}
