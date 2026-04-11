using System.Threading.Channels;
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

    /// <summary>Exposes the data source for batched spool consumer transactions.</summary>
    internal NpgsqlDataSource DataSource => _dataSource;

    public BandLeaderboardPersistence(NpgsqlDataSource dataSource, ILogger<BandLeaderboardPersistence> log)
    {
        _dataSource = dataSource;
        _log = log;
    }

    // ─── Per-band-type bounded channels for async batched writes ──────

    private record struct BandWorkItem(string SongId, string BandType, IReadOnlyList<BandLeaderboardEntry> Entries);

    private Dictionary<string, Channel<BandWorkItem>>? _channels;
    private List<Task>? _writerTasks;

    /// <summary>
    /// Start per-band-type writer tasks. Each band type (Duets, Trios, Quad)
    /// gets its own bounded channel and dedicated writer task so they don't
    /// block each other. Naturally extends when new band types are added.
    /// </summary>
    public void StartWriter(IEnumerable<string>? bandTypes = null, int channelCapacity = 64, int writeBatchSize = 10, CancellationToken ct = default)
    {
        var types = bandTypes ?? ["Band_Duets", "Band_Trios", "Band_Quad"];
        _channels = new Dictionary<string, Channel<BandWorkItem>>(StringComparer.OrdinalIgnoreCase);
        _writerTasks = new List<Task>();

        foreach (var bandType in types)
        {
            var channel = Channel.CreateBounded<BandWorkItem>(new BoundedChannelOptions(channelCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
            _channels[bandType] = channel;

            var task = Task.Run(async () =>
            {
                await RunBandWriterAsync(channel.Reader, writeBatchSize, ct);
            }, ct);
            _writerTasks.Add(task);
        }

        _log.LogInformation("Started {Count} per-band-type writers (capacity {Cap}, batch {Batch}).",
            _writerTasks.Count, channelCapacity, writeBatchSize);
    }

    /// <summary>Enqueue a band page for async persistence. Applies back-pressure when full.</summary>
    public async ValueTask EnqueueAsync(string songId, string bandType,
                                         IReadOnlyList<BandLeaderboardEntry> entries,
                                         CancellationToken ct = default)
    {
        if (_channels is null)
            throw new InvalidOperationException("Band writers not started. Call StartWriter() first.");

        if (!_channels.TryGetValue(bandType, out var channel))
        {
            _log.LogWarning("No band writer channel for {BandType}. Dropping page.", bandType);
            return;
        }

        await channel.Writer.WriteAsync(new BandWorkItem(songId, bandType, entries), ct);
    }

    /// <summary>Signal all band writers that no more items will arrive, then wait for drain.</summary>
    public async Task DrainWriterAsync()
    {
        if (_channels is null || _writerTasks is null) return;

        foreach (var channel in _channels.Values)
            channel.Writer.TryComplete();

        await Task.WhenAll(_writerTasks);

        _log.LogInformation("All per-band-type writers drained.");
        _channels = null;
        _writerTasks = null;
    }

    private async Task RunBandWriterAsync(ChannelReader<BandWorkItem> reader, int batchSize,
                                           CancellationToken ct)
    {
        var batch = new List<BandWorkItem>(batchSize);

        while (await reader.WaitToReadAsync(ct))
        {
            batch.Clear();
            while (batch.Count < batchSize && reader.TryRead(out var item))
                batch.Add(item);

            if (batch.Count == 0) continue;

            foreach (var item in batch)
            {
                try
                {
                    UpsertBandEntries(item.SongId, item.BandType, item.Entries);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Band writer error for {SongId}/{BandType}. Data will be retried next pass.",
                        item.SongId, item.BandType);
                }
            }
        }
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
                        song_id TEXT, band_type TEXT, team_key TEXT, instrument_combo TEXT,
                        team_members TEXT[],
                        score INT, base_score INT, instrument_bonus INT, overdrive_bonus INT,
                        accuracy INT, is_full_combo BOOLEAN, stars INT, difficulty INT,
                        season INT, rank INT, percentile DOUBLE PRECISION, end_time TEXT,
                        source TEXT, is_over_threshold BOOLEAN, ts TIMESTAMPTZ
                    ) ON COMMIT DROP
                    """;
                cmd.ExecuteNonQuery();
            }

            using (var writer = conn.BeginBinaryImport(
                "COPY _be_staging (song_id, band_type, team_key, instrument_combo, team_members, score, base_score, " +
                "instrument_bonus, overdrive_bonus, accuracy, is_full_combo, stars, difficulty, " +
                "season, rank, percentile, end_time, source, is_over_threshold, ts) FROM STDIN (FORMAT BINARY)"))
            {
                foreach (var e in entries)
                {
                    writer.StartRow();
                    writer.Write(songId, NpgsqlDbType.Text);
                    writer.Write(bandType, NpgsqlDbType.Text);
                    writer.Write(e.TeamKey, NpgsqlDbType.Text);
                    writer.Write(e.InstrumentCombo, NpgsqlDbType.Text);
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
                merged = cmd.ExecuteNonQuery();
            }

            // ── 3. COPY band_member_stats ──
            var allMemberStats = entries
                .Where(e => e.MemberStats.Count > 0)
                .SelectMany(e => e.MemberStats.Select(ms => (e.TeamKey, e.InstrumentCombo, ms)))
                .ToList();

            if (allMemberStats.Count > 0)
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        CREATE TEMP TABLE _bms_staging (
                            song_id TEXT, band_type TEXT, team_key TEXT, instrument_combo TEXT,
                            member_index INT, account_id TEXT, instrument_id INT,
                            score INT, accuracy INT, is_full_combo BOOLEAN,
                            stars INT, difficulty INT
                        ) ON COMMIT DROP
                        """;
                    cmd.ExecuteNonQuery();
                }

                using (var writer = conn.BeginBinaryImport(
                    "COPY _bms_staging (song_id, band_type, team_key, instrument_combo, member_index, account_id, " +
                    "instrument_id, score, accuracy, is_full_combo, stars, difficulty) FROM STDIN (FORMAT BINARY)"))
                {
                    foreach (var (teamKey, instrumentCombo, ms) in allMemberStats)
                    {
                        writer.StartRow();
                        writer.Write(songId, NpgsqlDbType.Text);
                        writer.Write(bandType, NpgsqlDbType.Text);
                        writer.Write(teamKey, NpgsqlDbType.Text);
                        writer.Write(instrumentCombo, NpgsqlDbType.Text);
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
                        INSERT INTO band_member_stats (song_id, band_type, team_key, instrument_combo, member_index,
                            account_id, instrument_id, score, accuracy, is_full_combo, stars, difficulty)
                        SELECT song_id, band_type, team_key, instrument_combo, member_index,
                            account_id, instrument_id, score, accuracy, is_full_combo, stars, difficulty
                        FROM _bms_staging
                        ON CONFLICT (song_id, band_type, team_key, instrument_combo, member_index) DO UPDATE SET
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
                .SelectMany(e => e.TeamMembers.Select(m => (AccountId: m, e.TeamKey, e.InstrumentCombo)))
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
                            account_id TEXT, song_id TEXT, band_type TEXT, team_key TEXT, instrument_combo TEXT
                        ) ON COMMIT DROP
                        """;
                    cmd.ExecuteNonQuery();
                }

                using (var writer = conn.BeginBinaryImport(
                    "COPY _bm_staging (account_id, song_id, band_type, team_key, instrument_combo) FROM STDIN (FORMAT BINARY)"))
                {
                    foreach (var (accountId, teamKey, instrumentCombo) in memberLookups)
                    {
                        writer.StartRow();
                        writer.Write(accountId, NpgsqlDbType.Text);
                        writer.Write(songId, NpgsqlDbType.Text);
                        writer.Write(bandType, NpgsqlDbType.Text);
                        writer.Write(teamKey, NpgsqlDbType.Text);
                        writer.Write(instrumentCombo, NpgsqlDbType.Text);
                    }
                    writer.Complete();
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT INTO band_members (account_id, song_id, band_type, team_key, instrument_combo)
                        SELECT account_id, song_id, band_type, team_key, instrument_combo FROM _bm_staging
                        ON CONFLICT (account_id, song_id, band_type, team_key, instrument_combo) DO NOTHING
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

    /// <summary>
    /// Upsert band entries using an externally managed connection/transaction.
    /// Used by <see cref="PostScrapeBandExtractor"/> to batch within its own transactions.
    /// Returns (bandRows, memberStatRows, memberLookupRows).
    /// </summary>
    public (int Bands, int Members, int Lookups) UpsertBandEntriesDirect(
        string songId, string bandType, IReadOnlyList<BandLeaderboardEntry> entries,
        NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        if (entries.Count == 0)
            return (0, 0, 0);

        var now = DateTimeOffset.UtcNow;

        // ── 1. Band entries staging ──
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
            foreach (var e in entries)
            {
                writer.StartRow();
                writer.Write(songId, NpgsqlDbType.Text);
                writer.Write(bandType, NpgsqlDbType.Text);
                writer.Write(e.TeamKey, NpgsqlDbType.Text);
                writer.Write(e.InstrumentCombo, NpgsqlDbType.Text);
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
                writer.Write(e.Source ?? "solo_extract", NpgsqlDbType.Text);
                writer.Write(e.IsOverThreshold, NpgsqlDbType.Boolean);
                writer.Write(now, NpgsqlDbType.TimestampTz);
            }
            writer.Complete();
        }

        int merged;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
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
            merged = cmd.ExecuteNonQuery();
        }

        // ── 2. Member stats ──
        int memberStatsCount = 0;
        var allMemberStats = entries
            .Where(e => e.MemberStats.Count > 0)
            .SelectMany(e => e.MemberStats.Select(ms => (e.TeamKey, e.InstrumentCombo, ms)))
            .ToList();

        if (allMemberStats.Count > 0)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DROP TABLE IF EXISTS _bms_staging";
                cmd.ExecuteNonQuery();
            }
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
                foreach (var (teamKey, instrumentCombo, ms) in allMemberStats)
                {
                    writer.StartRow();
                    writer.Write(songId, NpgsqlDbType.Text);
                    writer.Write(bandType, NpgsqlDbType.Text);
                    writer.Write(teamKey, NpgsqlDbType.Text);
                    writer.Write(instrumentCombo, NpgsqlDbType.Text);
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
                    INSERT INTO band_member_stats (song_id, band_type, team_key, instrument_combo, member_index,
                        account_id, instrument_id, score, accuracy, is_full_combo, stars, difficulty)
                    SELECT DISTINCT ON (song_id, band_type, team_key, instrument_combo, member_index)
                        song_id, band_type, team_key, instrument_combo, member_index,
                        account_id, instrument_id, score, accuracy, is_full_combo, stars, difficulty
                    FROM _bms_staging
                    ORDER BY song_id, band_type, team_key, instrument_combo, member_index
                    ON CONFLICT (song_id, band_type, team_key, instrument_combo, member_index) DO UPDATE SET
                        account_id = EXCLUDED.account_id,
                        instrument_id = EXCLUDED.instrument_id,
                        score = EXCLUDED.score,
                        accuracy = EXCLUDED.accuracy,
                        is_full_combo = EXCLUDED.is_full_combo,
                        stars = EXCLUDED.stars,
                        difficulty = EXCLUDED.difficulty
                    """;
                memberStatsCount = cmd.ExecuteNonQuery();
            }

            using (var cmd = conn.CreateCommand()) { cmd.Transaction = tx; cmd.CommandText = "DROP TABLE IF EXISTS _bms_staging"; cmd.ExecuteNonQuery(); }
        }

        // ── 3. Member lookups ──
        int lookupCount = 0;
        var memberLookups = entries
            .SelectMany(e => e.TeamMembers.Select(m => (AccountId: m, e.TeamKey, e.InstrumentCombo)))
            .Where(x => !string.IsNullOrEmpty(x.AccountId))
            .Distinct()
            .ToList();

        if (memberLookups.Count > 0)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DROP TABLE IF EXISTS _bm_staging";
                cmd.ExecuteNonQuery();
            }
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
                foreach (var (accountId, teamKey, instrumentCombo) in memberLookups)
                {
                    writer.StartRow();
                    writer.Write(accountId, NpgsqlDbType.Text);
                    writer.Write(songId, NpgsqlDbType.Text);
                    writer.Write(bandType, NpgsqlDbType.Text);
                    writer.Write(teamKey, NpgsqlDbType.Text);
                    writer.Write(instrumentCombo, NpgsqlDbType.Text);
                }
                writer.Complete();
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO band_members (account_id, song_id, band_type, team_key, instrument_combo)
                    SELECT DISTINCT account_id, song_id, band_type, team_key, instrument_combo FROM _bm_staging
                    ON CONFLICT (account_id, song_id, band_type, team_key, instrument_combo) DO NOTHING
                    """;
                lookupCount = cmd.ExecuteNonQuery();
            }

            using (var cmd = conn.CreateCommand()) { cmd.Transaction = tx; cmd.CommandText = "DROP TABLE IF EXISTS _bm_staging"; cmd.ExecuteNonQuery(); }
        }

        using (var cmd = conn.CreateCommand()) { cmd.Transaction = tx; cmd.CommandText = "DROP TABLE IF EXISTS _be_staging"; cmd.ExecuteNonQuery(); }

        return (merged, memberStatsCount, lookupCount);
    }
}
