using System.Buffers.Binary;
using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// Band leaderboard spool — thin wrapper around <see cref="SpoolWriter{T}"/>
/// providing <see cref="BandLeaderboardEntry"/>-specific binary serialization
/// and a flush delegate that batch-upserts via <see cref="BandLeaderboardPersistence"/>.
/// </summary>
public static class BandSpoolWriterFactory
{
    public static SpoolWriter<BandLeaderboardEntry> Create(ILogger log, BandLeaderboardPersistence persistence, string? baseDirectory = null)
    {
        return new SpoolWriter<BandLeaderboardEntry>(
            log, "band",
            serialize: SerializeBandPage,
            deserialize: DeserializeBandPage,
            flush: (bandType, batch) => FlushBandBatch(log, persistence, bandType, batch),
            baseDirectory: baseDirectory);
    }

    private static void FlushBandBatch(ILogger log, BandLeaderboardPersistence persistence, string bandType,
                                        List<(string SongId, IReadOnlyList<BandLeaderboardEntry> Entries)> batch)
    {
        // Flatten all entries across all songs into one list for bulk COPY.
        // This does 1 staging cycle per table instead of N (one per song).
        var allEntries = new List<(string SongId, BandLeaderboardEntry Entry)>();
        foreach (var (songId, entries) in batch)
            foreach (var entry in entries)
                allEntries.Add((songId, entry));

        if (allEntries.Count == 0) return;

        try
        {
            using var conn = persistence.DataSource.OpenConnection();
            using var tx = conn.BeginTransaction();
            using (var sc = conn.CreateCommand())
            {
                sc.Transaction = tx;
                sc.CommandText = "SET LOCAL synchronous_commit = off";
                sc.ExecuteNonQuery();
            }

            var now = System.DateTimeOffset.UtcNow;

            // ── 1. Band entries: single COPY + merge ──
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DROP TABLE IF EXISTS _be_staging";
                cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    CREATE TEMP TABLE _be_staging (
                        song_id TEXT, band_type TEXT, team_key TEXT, instrument_combo TEXT,
                        team_members TEXT[],
                        score INT, base_score INT, instrument_bonus INT, overdrive_bonus INT,
                        accuracy INT, is_full_combo BOOLEAN, stars INT, difficulty INT,
                        season INT, rank INT, percentile DOUBLE PRECISION, end_time TEXT,
                        source TEXT, is_over_threshold BOOLEAN, ts TIMESTAMPTZ
                    )
                    """;
                cmd.ExecuteNonQuery();
            }

            using (var writer = conn.BeginBinaryImport(
                "COPY _be_staging (song_id, band_type, team_key, instrument_combo, team_members, score, base_score, " +
                "instrument_bonus, overdrive_bonus, accuracy, is_full_combo, stars, difficulty, " +
                "season, rank, percentile, end_time, source, is_over_threshold, ts) FROM STDIN (FORMAT BINARY)"))
            {
                foreach (var (songId, e) in allEntries)
                {
                    writer.StartRow();
                    writer.Write(songId, NpgsqlTypes.NpgsqlDbType.Text);
                    writer.Write(bandType, NpgsqlTypes.NpgsqlDbType.Text);
                    writer.Write(e.TeamKey, NpgsqlTypes.NpgsqlDbType.Text);
                    writer.Write(e.InstrumentCombo, NpgsqlTypes.NpgsqlDbType.Text);
                    writer.Write(e.TeamMembers, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text);
                    writer.Write(e.Score, NpgsqlTypes.NpgsqlDbType.Integer);
                    WriteNullableInt(writer, e.BaseScore);
                    WriteNullableInt(writer, e.InstrumentBonus);
                    WriteNullableInt(writer, e.OverdriveBonus);
                    writer.Write(e.Accuracy, NpgsqlTypes.NpgsqlDbType.Integer);
                    writer.Write(e.IsFullCombo, NpgsqlTypes.NpgsqlDbType.Boolean);
                    writer.Write(e.Stars, NpgsqlTypes.NpgsqlDbType.Integer);
                    writer.Write(e.Difficulty, NpgsqlTypes.NpgsqlDbType.Integer);
                    writer.Write(e.Season, NpgsqlTypes.NpgsqlDbType.Integer);
                    writer.Write(e.Rank, NpgsqlTypes.NpgsqlDbType.Integer);
                    writer.Write(e.Percentile, NpgsqlTypes.NpgsqlDbType.Double);
                    if (e.EndTime is not null) writer.Write(e.EndTime, NpgsqlTypes.NpgsqlDbType.Text);
                    else writer.WriteNull();
                    writer.Write(e.Source ?? "scrape", NpgsqlTypes.NpgsqlDbType.Text);
                    writer.Write(e.IsOverThreshold, NpgsqlTypes.NpgsqlDbType.Boolean);
                    writer.Write(now, NpgsqlTypes.NpgsqlDbType.TimestampTz);
                }
                writer.Complete();
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandTimeout = 0; // Unlimited — bulk merge of millions of rows can exceed 10 min
                cmd.CommandText = """
                    INSERT INTO band_entries (song_id, band_type, team_key, instrument_combo, team_members, score,
                        base_score, instrument_bonus, overdrive_bonus, accuracy, is_full_combo,
                        stars, difficulty, season, rank, percentile, end_time, source,
                        is_over_threshold, first_seen_at, last_updated_at)
                    SELECT DISTINCT ON (song_id, band_type, team_key, instrument_combo)
                        song_id, band_type, team_key, instrument_combo, team_members, score,
                        base_score, instrument_bonus, overdrive_bonus, accuracy, is_full_combo,
                        stars, difficulty, season, rank, percentile, end_time, source,
                        is_over_threshold, ts, ts
                    FROM _be_staging
                    ORDER BY song_id, band_type, team_key, instrument_combo, score DESC
                    ON CONFLICT (song_id, band_type, team_key, instrument_combo) DO UPDATE SET
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
                cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand()) { cmd.Transaction = tx; cmd.CommandText = "DROP TABLE IF EXISTS _be_staging"; cmd.ExecuteNonQuery(); }

            // ── 2. Member stats: single COPY + merge ──
            var allMemberStats = allEntries
                .Where(x => x.Entry.MemberStats.Count > 0)
                .SelectMany(x => x.Entry.MemberStats.Select(ms => (x.SongId, x.Entry.TeamKey, x.Entry.InstrumentCombo, ms)))
                .ToList();

            if (allMemberStats.Count > 0)
            {
                using (var cmd = conn.CreateCommand()) { cmd.Transaction = tx; cmd.CommandText = "DROP TABLE IF EXISTS _bms_staging"; cmd.ExecuteNonQuery(); }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        CREATE TEMP TABLE _bms_staging (
                            song_id TEXT, band_type TEXT, team_key TEXT, instrument_combo TEXT,
                            member_index INT, account_id TEXT, instrument_id INT,
                            score INT, accuracy INT, is_full_combo BOOLEAN,
                            stars INT, difficulty INT
                        )
                        """;
                    cmd.ExecuteNonQuery();
                }
                using (var writer = conn.BeginBinaryImport(
                    "COPY _bms_staging (song_id, band_type, team_key, instrument_combo, member_index, account_id, " +
                    "instrument_id, score, accuracy, is_full_combo, stars, difficulty) FROM STDIN (FORMAT BINARY)"))
                {
                    foreach (var (songId, teamKey, instrumentCombo, ms) in allMemberStats)
                    {
                        writer.StartRow();
                        writer.Write(songId, NpgsqlTypes.NpgsqlDbType.Text);
                        writer.Write(bandType, NpgsqlTypes.NpgsqlDbType.Text);
                        writer.Write(teamKey, NpgsqlTypes.NpgsqlDbType.Text);
                        writer.Write(instrumentCombo, NpgsqlTypes.NpgsqlDbType.Text);
                        writer.Write(ms.MemberIndex, NpgsqlTypes.NpgsqlDbType.Integer);
                        writer.Write(ms.AccountId, NpgsqlTypes.NpgsqlDbType.Text);
                        writer.Write(ms.InstrumentId, NpgsqlTypes.NpgsqlDbType.Integer);
                        writer.Write(ms.Score, NpgsqlTypes.NpgsqlDbType.Integer);
                        writer.Write(ms.Accuracy, NpgsqlTypes.NpgsqlDbType.Integer);
                        writer.Write(ms.IsFullCombo, NpgsqlTypes.NpgsqlDbType.Boolean);
                        writer.Write(ms.Stars, NpgsqlTypes.NpgsqlDbType.Integer);
                        writer.Write(ms.Difficulty, NpgsqlTypes.NpgsqlDbType.Integer);
                    }
                    writer.Complete();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandTimeout = 0; // Unlimited — bulk merge can exceed 10 min
                    cmd.CommandText = """
                        INSERT INTO band_member_stats (song_id, band_type, team_key, instrument_combo, member_index,
                            account_id, instrument_id, score, accuracy, is_full_combo, stars, difficulty)
                        SELECT DISTINCT ON (song_id, band_type, team_key, instrument_combo, member_index)
                            song_id, band_type, team_key, instrument_combo, member_index,
                            account_id, instrument_id, score, accuracy, is_full_combo, stars, difficulty
                        FROM _bms_staging
                        ORDER BY song_id, band_type, team_key, instrument_combo, member_index
                        ON CONFLICT (song_id, band_type, team_key, instrument_combo, member_index) DO UPDATE SET
                            account_id = EXCLUDED.account_id, instrument_id = EXCLUDED.instrument_id,
                            score = EXCLUDED.score, accuracy = EXCLUDED.accuracy,
                            is_full_combo = EXCLUDED.is_full_combo, stars = EXCLUDED.stars,
                            difficulty = EXCLUDED.difficulty
                        """;
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand()) { cmd.Transaction = tx; cmd.CommandText = "DROP TABLE IF EXISTS _bms_staging"; cmd.ExecuteNonQuery(); }
            }

            // ── 3. Member lookups: single COPY + merge ──
            var memberLookups = allEntries
                .SelectMany(x => x.Entry.TeamMembers.Select(m => (AccountId: m, x.SongId, x.Entry.TeamKey, x.Entry.InstrumentCombo)))
                .Where(x => !string.IsNullOrEmpty(x.AccountId))
                .Distinct()
                .ToList();

            if (memberLookups.Count > 0)
            {
                using (var cmd = conn.CreateCommand()) { cmd.Transaction = tx; cmd.CommandText = "DROP TABLE IF EXISTS _bm_staging"; cmd.ExecuteNonQuery(); }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        CREATE TEMP TABLE _bm_staging (
                            account_id TEXT, song_id TEXT, band_type TEXT, team_key TEXT, instrument_combo TEXT
                        )
                        """;
                    cmd.ExecuteNonQuery();
                }
                using (var writer = conn.BeginBinaryImport(
                    "COPY _bm_staging (account_id, song_id, band_type, team_key, instrument_combo) FROM STDIN (FORMAT BINARY)"))
                {
                    foreach (var (accountId, songId, teamKey, instrumentCombo) in memberLookups)
                    {
                        writer.StartRow();
                        writer.Write(accountId, NpgsqlTypes.NpgsqlDbType.Text);
                        writer.Write(songId, NpgsqlTypes.NpgsqlDbType.Text);
                        writer.Write(bandType, NpgsqlTypes.NpgsqlDbType.Text);
                        writer.Write(teamKey, NpgsqlTypes.NpgsqlDbType.Text);
                        writer.Write(instrumentCombo, NpgsqlTypes.NpgsqlDbType.Text);
                    }
                    writer.Complete();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandTimeout = 0; // Unlimited — bulk merge can exceed 10 min
                    cmd.CommandText = """
                        INSERT INTO band_members (account_id, song_id, band_type, team_key, instrument_combo)
                        SELECT account_id, song_id, band_type, team_key, instrument_combo FROM _bm_staging
                        ON CONFLICT (account_id, song_id, band_type, team_key, instrument_combo) DO NOTHING
                        """;
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand()) { cmd.Transaction = tx; cmd.CommandText = "DROP TABLE IF EXISTS _bm_staging"; cmd.ExecuteNonQuery(); }
            }

            tx.Commit();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Spool [band] flush failed for {BandType} ({Songs} songs, {Entries:N0} entries). Data will be re-scraped next pass.",
                bandType, batch.Count, allEntries.Count);
        }
    }

    private static void WriteNullableInt(Npgsql.NpgsqlBinaryImporter writer, int? value)
    {
        if (value.HasValue) writer.Write(value.Value, NpgsqlTypes.NpgsqlDbType.Integer);
        else writer.WriteNull();
    }

    // ── Band serialization ──────────────────────────────────────

    /// <summary>Serialize delegate — exposed for harness testing.</summary>
    public static void TestSerialize(MemoryStream buf, byte[] header, string songId, IReadOnlyList<BandLeaderboardEntry> entries)
        => SerializeBandPage(buf, header, songId, entries);

    /// <summary>Deserialize delegate — exposed for harness testing.</summary>
    public static (string SongId, IReadOnlyList<BandLeaderboardEntry> Entries) TestDeserialize(Stream stream, byte[] header)
        => DeserializeBandPage(stream, header);

    private static void SerializeBandPage(MemoryStream buf, byte[] header, string songId, IReadOnlyList<BandLeaderboardEntry> entries)
    {
        SpoolWriter<BandLeaderboardEntry>.WriteString(buf, header, songId);
        BinaryPrimitives.WriteInt32LittleEndian(header, entries.Count);
        buf.Write(header, 0, 4);

        foreach (var e in entries)
            SerializeEntry(buf, header, e);
    }

    private static (string SongId, IReadOnlyList<BandLeaderboardEntry> Entries) DeserializeBandPage(Stream stream, byte[] header)
    {
        var songId = SpoolWriter<BandLeaderboardEntry>.ReadString(stream, header);
        SpoolWriter<BandLeaderboardEntry>.ReadExact(stream, header.AsSpan(0, 4));
        int count = BinaryPrimitives.ReadInt32LittleEndian(header);

        var entries = new BandLeaderboardEntry[count];
        for (int i = 0; i < count; i++)
            entries[i] = DeserializeEntry(stream, header);
        return (songId, entries);
    }

    private static void SerializeEntry(MemoryStream buf, byte[] header, BandLeaderboardEntry e)
    {
        SpoolWriter<BandLeaderboardEntry>.WriteString(buf, header, e.TeamKey);
        SpoolWriter<BandLeaderboardEntry>.WriteString(buf, header, e.InstrumentCombo);

        // TeamMembers array
        SpoolWriter<BandLeaderboardEntry>.WriteInt32(buf, header, e.TeamMembers.Length);
        foreach (var m in e.TeamMembers)
            SpoolWriter<BandLeaderboardEntry>.WriteString(buf, header, m);

        SpoolWriter<BandLeaderboardEntry>.WriteInt32(buf, header, e.Score);
        SpoolWriter<BandLeaderboardEntry>.WriteNullableInt32(buf, header, e.BaseScore);
        SpoolWriter<BandLeaderboardEntry>.WriteNullableInt32(buf, header, e.InstrumentBonus);
        SpoolWriter<BandLeaderboardEntry>.WriteNullableInt32(buf, header, e.OverdriveBonus);
        SpoolWriter<BandLeaderboardEntry>.WriteInt32(buf, header, e.Accuracy);
        buf.WriteByte(e.IsFullCombo ? (byte)1 : (byte)0);
        SpoolWriter<BandLeaderboardEntry>.WriteInt32(buf, header, e.Stars);
        SpoolWriter<BandLeaderboardEntry>.WriteInt32(buf, header, e.Difficulty);
        SpoolWriter<BandLeaderboardEntry>.WriteInt32(buf, header, e.Season);
        SpoolWriter<BandLeaderboardEntry>.WriteInt32(buf, header, e.Rank);
        BinaryPrimitives.WriteDoubleLittleEndian(header, e.Percentile);
        buf.Write(header, 0, 8);
        SpoolWriter<BandLeaderboardEntry>.WriteNullableString(buf, header, e.EndTime);
        SpoolWriter<BandLeaderboardEntry>.WriteNullableString(buf, header, e.Source);
        buf.WriteByte(e.IsOverThreshold ? (byte)1 : (byte)0);

        // MemberStats
        SpoolWriter<BandLeaderboardEntry>.WriteInt32(buf, header, e.MemberStats.Count);
        foreach (var ms in e.MemberStats)
        {
            SpoolWriter<BandLeaderboardEntry>.WriteInt32(buf, header, ms.MemberIndex);
            SpoolWriter<BandLeaderboardEntry>.WriteString(buf, header, ms.AccountId);
            SpoolWriter<BandLeaderboardEntry>.WriteInt32(buf, header, ms.InstrumentId);
            SpoolWriter<BandLeaderboardEntry>.WriteInt32(buf, header, ms.Score);
            SpoolWriter<BandLeaderboardEntry>.WriteInt32(buf, header, ms.Accuracy);
            buf.WriteByte(ms.IsFullCombo ? (byte)1 : (byte)0);
            SpoolWriter<BandLeaderboardEntry>.WriteInt32(buf, header, ms.Stars);
            SpoolWriter<BandLeaderboardEntry>.WriteInt32(buf, header, ms.Difficulty);
        }
    }

    private static BandLeaderboardEntry DeserializeEntry(Stream stream, byte[] header)
    {
        var entry = new BandLeaderboardEntry
        {
            TeamKey = SpoolWriter<BandLeaderboardEntry>.ReadString(stream, header),
            InstrumentCombo = SpoolWriter<BandLeaderboardEntry>.ReadString(stream, header),
        };

        int memberCount = SpoolWriter<BandLeaderboardEntry>.ReadInt32(stream, header);
        entry.TeamMembers = new string[memberCount];
        for (int i = 0; i < memberCount; i++)
            entry.TeamMembers[i] = SpoolWriter<BandLeaderboardEntry>.ReadString(stream, header);

        entry.Score = SpoolWriter<BandLeaderboardEntry>.ReadInt32(stream, header);
        entry.BaseScore = SpoolWriter<BandLeaderboardEntry>.ReadNullableInt32(stream, header);
        entry.InstrumentBonus = SpoolWriter<BandLeaderboardEntry>.ReadNullableInt32(stream, header);
        entry.OverdriveBonus = SpoolWriter<BandLeaderboardEntry>.ReadNullableInt32(stream, header);
        entry.Accuracy = SpoolWriter<BandLeaderboardEntry>.ReadInt32(stream, header);

        SpoolWriter<BandLeaderboardEntry>.ReadExact(stream, header.AsSpan(0, 1));
        entry.IsFullCombo = header[0] == 1;

        entry.Stars = SpoolWriter<BandLeaderboardEntry>.ReadInt32(stream, header);
        entry.Difficulty = SpoolWriter<BandLeaderboardEntry>.ReadInt32(stream, header);
        entry.Season = SpoolWriter<BandLeaderboardEntry>.ReadInt32(stream, header);
        entry.Rank = SpoolWriter<BandLeaderboardEntry>.ReadInt32(stream, header);
        entry.Percentile = SpoolWriter<BandLeaderboardEntry>.ReadDouble(stream, header);
        entry.EndTime = SpoolWriter<BandLeaderboardEntry>.ReadNullableString(stream, header);
        entry.Source = SpoolWriter<BandLeaderboardEntry>.ReadNullableString(stream, header) ?? "scrape";

        SpoolWriter<BandLeaderboardEntry>.ReadExact(stream, header.AsSpan(0, 1));
        entry.IsOverThreshold = header[0] == 1;

        int statsCount = SpoolWriter<BandLeaderboardEntry>.ReadInt32(stream, header);
        entry.MemberStats = new List<BandMemberStats>(statsCount);
        for (int i = 0; i < statsCount; i++)
        {
            var ms = new BandMemberStats
            {
                MemberIndex = SpoolWriter<BandLeaderboardEntry>.ReadInt32(stream, header),
                AccountId = SpoolWriter<BandLeaderboardEntry>.ReadString(stream, header),
                InstrumentId = SpoolWriter<BandLeaderboardEntry>.ReadInt32(stream, header),
                Score = SpoolWriter<BandLeaderboardEntry>.ReadInt32(stream, header),
                Accuracy = SpoolWriter<BandLeaderboardEntry>.ReadInt32(stream, header),
            };
            SpoolWriter<BandLeaderboardEntry>.ReadExact(stream, header.AsSpan(0, 1));
            ms.IsFullCombo = header[0] == 1;
            ms.Stars = SpoolWriter<BandLeaderboardEntry>.ReadInt32(stream, header);
            ms.Difficulty = SpoolWriter<BandLeaderboardEntry>.ReadInt32(stream, header);
            entry.MemberStats.Add(ms);
        }

        return entry;
    }
}
