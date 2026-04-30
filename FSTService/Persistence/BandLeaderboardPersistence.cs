using System.Text.Json;
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

    internal const string BandTeamMembershipTable = "band_team_membership";
    internal const string BandTeamMembershipStateTable = "band_team_membership_state";
    internal const string BandTeamConfigurationTable = "band_team_configurations";

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

        var impactedTeamKeys = entries
            .Select(static entry => entry.TeamKey)
            .Where(static teamKey => !string.IsNullOrWhiteSpace(teamKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

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

            RebuildBandTeamMembershipForTeams(conn, tx, bandType, impactedTeamKeys);

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
        NpgsqlConnection conn, NpgsqlTransaction tx,
        bool rebuildTeamMembership = true)
    {
        if (entries.Count == 0)
            return (0, 0, 0);

        var impactedTeamKeys = entries
            .Select(static entry => entry.TeamKey)
            .Where(static teamKey => !string.IsNullOrWhiteSpace(teamKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

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

        if (rebuildTeamMembership)
            RebuildBandTeamMembershipForTeams(conn, tx, bandType, impactedTeamKeys);

        return (merged, memberStatsCount, lookupCount);
    }

    // ── Band pruning ──────────────────────────────────────────────

    /// <summary>
    /// Prune excess band entries per song and band type.
    /// For each (song_id, band_type): keep all over-threshold entries at the top,
    /// plus the next <paramref name="maxValidEntries"/> valid entries, plus any
    /// entry containing a registered user. Delete everything else, cascading to
    /// <c>band_member_stats</c> and <c>band_members</c>.
    /// </summary>
    /// <returns>Total band_entries rows deleted.</returns>
    public int PruneBandEntries(IReadOnlySet<string> registeredIds, int maxValidEntries = 10000)
        => PruneBandEntriesDetailed(registeredIds, maxValidEntries).DeletedEntries;

    public BandPruneResult PruneBandEntriesDetailed(IReadOnlySet<string> registeredIds, int maxValidEntries = 10000)
    {
        if (maxValidEntries <= 0) return BandPruneResult.Empty;

        var affectedTeamsByBandType = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var affectedAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int deleted;
        int statsDeleted = 0;
        int lookupsDeleted = 0;

        using var conn = _dataSource.OpenConnection();

        using (var tx = conn.BeginTransaction())
        {
            using (var sc = conn.CreateCommand()) { sc.Transaction = tx; sc.CommandText = "SET LOCAL synchronous_commit = off"; sc.ExecuteNonQuery(); }

            // Build a temp table of registered account IDs for the JOIN
            using (var cmd = conn.CreateCommand()) { cmd.Transaction = tx; cmd.CommandText = "CREATE TEMP TABLE _prune_reg (account_id TEXT PRIMARY KEY) ON COMMIT DROP"; cmd.ExecuteNonQuery(); }
            if (registeredIds.Count > 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO _prune_reg VALUES (@id) ON CONFLICT DO NOTHING";
                var p = cmd.Parameters.Add("id", NpgsqlDbType.Text);
                cmd.Prepare();
                foreach (var id in registeredIds) { p.Value = id; cmd.ExecuteNonQuery(); }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    CREATE TEMP TABLE _band_prune_deleted_keys (
                        song_id TEXT NOT NULL,
                        band_type TEXT NOT NULL,
                        team_key TEXT NOT NULL,
                        instrument_combo TEXT NOT NULL
                    ) ON COMMIT DROP
                    """;
                cmd.ExecuteNonQuery();
            }

            // Identify entries to DELETE across all songs and band types in one pass.
            // The CTE computes a rank within each (song_id, band_type) partition,
            // finds the first valid entry (is_over_threshold = false), then marks
            // everything beyond (over_threshold_count + maxValidEntries) for deletion
            // — unless the team contains a registered user. Deleted keys are captured
            // so member cleanup can be targeted instead of scanning every band member row.
            using (var deleteCmd = conn.CreateCommand())
            {
                deleteCmd.Transaction = tx;
                deleteCmd.CommandTimeout = 0;
                deleteCmd.CommandText = """
                    WITH ranked AS (
                        SELECT song_id, band_type, team_key, instrument_combo, is_over_threshold,
                               ROW_NUMBER() OVER (
                                   PARTITION BY song_id, band_type
                                   ORDER BY score DESC, COALESCE(end_time, '') ASC
                               ) AS rn
                        FROM band_entries
                    ),
                    boundaries AS (
                        SELECT song_id, band_type,
                               COALESCE(MIN(rn) FILTER (WHERE is_over_threshold = false), 2147483647) AS first_valid_rn
                        FROM ranked
                        GROUP BY song_id, band_type
                    ),
                    to_delete AS (
                        SELECT r.song_id, r.band_type, r.team_key, r.instrument_combo
                        FROM ranked r
                        JOIN boundaries b ON r.song_id = b.song_id AND r.band_type = b.band_type
                        WHERE r.rn >= b.first_valid_rn + @maxValid
                          AND NOT EXISTS (
                              SELECT 1 FROM band_members bm
                              JOIN _prune_reg pr ON bm.account_id = pr.account_id
                              WHERE bm.song_id = r.song_id AND bm.band_type = r.band_type
                                AND bm.team_key = r.team_key AND bm.instrument_combo = r.instrument_combo
                          )
                    ),
                    deleted AS (
                        DELETE FROM band_entries be
                        USING to_delete td
                        WHERE be.song_id = td.song_id AND be.band_type = td.band_type
                          AND be.team_key = td.team_key AND be.instrument_combo = td.instrument_combo
                        RETURNING be.song_id, be.band_type, be.team_key, be.instrument_combo
                    )
                    INSERT INTO _band_prune_deleted_keys (song_id, band_type, team_key, instrument_combo)
                    SELECT song_id, band_type, team_key, instrument_combo
                    FROM deleted
                    """;
                deleteCmd.Parameters.AddWithValue("maxValid", maxValidEntries);
                deleted = deleteCmd.ExecuteNonQuery();
            }

            if (deleted > 0)
            {
                using (var affectedCmd = conn.CreateCommand())
                {
                    affectedCmd.Transaction = tx;
                    affectedCmd.CommandText = """
                        SELECT band_type, team_key
                        FROM _band_prune_deleted_keys
                        GROUP BY band_type, team_key
                        ORDER BY band_type, team_key
                        """;
                    using var reader = affectedCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var bandType = reader.GetString(0);
                        var teamKey = reader.GetString(1);
                        if (!affectedTeamsByBandType.TryGetValue(bandType, out var teamKeys))
                        {
                            teamKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            affectedTeamsByBandType[bandType] = teamKeys;
                        }

                        teamKeys.Add(teamKey);
                        foreach (var accountId in SplitTeamKey(teamKey))
                            affectedAccounts.Add(accountId);
                    }
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandTimeout = 0;
                    cmd.CommandText = """
                        DELETE FROM band_member_stats bms
                        USING _band_prune_deleted_keys d
                        WHERE bms.song_id = d.song_id
                          AND bms.band_type = d.band_type
                          AND bms.team_key = d.team_key
                          AND bms.instrument_combo = d.instrument_combo
                        """;
                    statsDeleted = cmd.ExecuteNonQuery();
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandTimeout = 0;
                    cmd.CommandText = """
                        DELETE FROM band_members bm
                        USING _band_prune_deleted_keys d
                        WHERE bm.song_id = d.song_id
                          AND bm.band_type = d.band_type
                          AND bm.team_key = d.team_key
                          AND bm.instrument_combo = d.instrument_combo
                        """;
                    lookupsDeleted = cmd.ExecuteNonQuery();
                }

                DeleteBandTeamMembershipStateForAccounts(conn, tx, affectedAccounts);
            }

            tx.Commit();
        }

        if (deleted > 0)
        {
            foreach (var (bandType, teamKeys) in affectedTeamsByBandType)
            {
                RebuildBandTeamMembershipForTeams(bandType, teamKeys.ToArray());
            }

            MarkBandTeamMembershipStateForAccounts(affectedAccounts);

            _log.LogInformation(
                "Pruned {Entries:N0} band entries, {Stats:N0} member stats, {Lookups:N0} member lookups; rebuilt membership summaries for {TeamCount:N0} team(s).",
                deleted,
                statsDeleted,
                lookupsDeleted,
                affectedTeamsByBandType.Sum(static kvp => kvp.Value.Count));
        }

        return new BandPruneResult(
            deleted,
            statsDeleted,
            lookupsDeleted,
            affectedTeamsByBandType.ToDictionary(
                static kvp => kvp.Key,
                static kvp => (IReadOnlyCollection<string>)kvp.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase));
    }

    private void MarkBandTeamMembershipStateForAccounts(IReadOnlyCollection<string> accountIds)
    {
        if (accountIds.Count == 0)
            return;

        using var conn = _dataSource.OpenConnection();
        using var tx = conn.BeginTransaction();
        UpsertBandTeamMembershipStateForAccounts(conn, tx, accountIds);
        tx.Commit();
    }

    private static void DeleteBandTeamMembershipStateForAccounts(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        IReadOnlyCollection<string> accountIds)
    {
        if (accountIds.Count == 0)
            return;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DELETE FROM {BandTeamMembershipStateTable} WHERE account_id = ANY(@accountIds)";
        cmd.Parameters.AddWithValue("accountIds", accountIds.ToArray());
        cmd.ExecuteNonQuery();
    }

    private static void UpsertBandTeamMembershipStateForAccounts(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        IReadOnlyCollection<string> accountIds)
    {
        if (accountIds.Count == 0)
            return;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            INSERT INTO {BandTeamMembershipStateTable} (account_id, rebuilt_at)
            SELECT DISTINCT unnest(@accountIds), @rebuiltAt
            ON CONFLICT (account_id) DO UPDATE SET rebuilt_at = EXCLUDED.rebuilt_at
            """;
        cmd.Parameters.AddWithValue("accountIds", accountIds.ToArray());
        cmd.Parameters.AddWithValue("rebuiltAt", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    internal static void RebuildBandTeamMembershipForAccount(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string accountId)
    {
        LockBandTeamMembershipRebuild(conn, tx);
        DeleteBandTeamMembershipForAccount(conn, tx, accountId);

        var countRows = new List<BandTeamMembershipCountRow>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT account_id, band_type, team_key, instrument_combo, COUNT(*)
                FROM band_members
                WHERE account_id = @accountId
                GROUP BY account_id, band_type, team_key, instrument_combo
                ORDER BY account_id, band_type, team_key, instrument_combo
                """;
            cmd.Parameters.AddWithValue("accountId", accountId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                countRows.Add(new BandTeamMembershipCountRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    reader.GetInt32(4)));
            }
        }

        UpsertBandTeamMembershipRows(conn, tx, BuildBandTeamMembershipWriteRows(conn, tx, countRows));
        RebuildBandTeamConfigurationsForTeams(conn, tx, countRows
            .GroupBy(static row => row.BandType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyCollection<string>)group.Select(static row => row.TeamKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase));
    }

    public int RebuildBandTeamMembershipForTeams(
        string bandType,
        IReadOnlyCollection<string> teamKeys,
        int maxDeadlockRetries = 3)
    {
        if (teamKeys.Count == 0)
            return 0;

        var sortedTeamKeys = teamKeys
            .Where(static teamKey => !string.IsNullOrWhiteSpace(teamKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static teamKey => teamKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (sortedTeamKeys.Length == 0)
            return 0;

        return ExecuteWithDeadlockRetry(() =>
        {
            using var conn = _dataSource.OpenConnection();
            using var tx = conn.BeginTransaction();
            var rebuilt = RebuildBandTeamMembershipForTeams(conn, tx, bandType, sortedTeamKeys);
            tx.Commit();
            return rebuilt;
        }, maxDeadlockRetries);
    }

    internal static int RebuildBandTeamMembershipForTeams(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string bandType,
        IReadOnlyCollection<string> teamKeys)
    {
        if (teamKeys.Count == 0)
            return 0;

        LockBandTeamMembershipRebuild(conn, tx);
        DeleteBandTeamMembershipForTeams(conn, tx, bandType, teamKeys);

        var countRows = new List<BandTeamMembershipCountRow>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT account_id, band_type, team_key, instrument_combo, COUNT(*)
                FROM band_members
                WHERE band_type = @bandType
                  AND team_key = ANY(@teamKeys)
                GROUP BY account_id, band_type, team_key, instrument_combo
                ORDER BY account_id, band_type, team_key, instrument_combo
                """;
            cmd.Parameters.AddWithValue("bandType", bandType);
            cmd.Parameters.AddWithValue("teamKeys", teamKeys.ToArray());

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                countRows.Add(new BandTeamMembershipCountRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    reader.GetInt32(4)));
            }
        }

        var rows = BuildBandTeamMembershipWriteRows(conn, tx, countRows);
        UpsertBandTeamMembershipRows(conn, tx, rows);
        RebuildBandTeamConfigurationsForTeams(conn, tx, bandType, teamKeys);
        return rows.Count;
    }

    internal static int RebuildBandTeamConfigurationsForTeams(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> teamKeysByBandType)
    {
        var rebuilt = 0;
        foreach (var (bandType, teamKeys) in teamKeysByBandType)
            rebuilt += RebuildBandTeamConfigurationsForTeams(conn, tx, bandType, teamKeys);
        return rebuilt;
    }

    internal static int RebuildBandTeamConfigurationsForTeams(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string bandType,
        IReadOnlyCollection<string> teamKeys)
    {
        var sortedTeamKeys = teamKeys
            .Where(static teamKey => !string.IsNullOrWhiteSpace(teamKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static teamKey => teamKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sortedTeamKeys.Length == 0)
            return 0;

        using (var deleteCmd = conn.CreateCommand())
        {
            deleteCmd.Transaction = tx;
            deleteCmd.CommandText = $"DELETE FROM {BandTeamConfigurationTable} WHERE band_type = @bandType AND team_key = ANY(@teamKeys)";
            deleteCmd.Parameters.AddWithValue("bandType", bandType);
            deleteCmd.Parameters.AddWithValue("teamKeys", sortedTeamKeys);
            deleteCmd.ExecuteNonQuery();
        }

        var expectedMembers = BandInstrumentMapping.ExpectedMemberCount(bandType);
        if (expectedMembers <= 0)
            return 0;

        using var insertCmd = conn.CreateCommand();
        insertCmd.Transaction = tx;
        insertCmd.CommandText = $"""
            WITH mapped AS (
                SELECT
                    song_id,
                    band_type,
                    team_key,
                    instrument_combo,
                    account_id,
                    CASE instrument_id
                        WHEN 0 THEN 'Solo_Guitar'
                        WHEN 1 THEN 'Solo_Bass'
                        WHEN 3 THEN 'Solo_Drums'
                        WHEN 2 THEN 'Solo_Vocals'
                        WHEN 4 THEN 'Solo_PeripheralGuitar'
                        WHEN 5 THEN 'Solo_PeripheralBass'
                        WHEN 7 THEN 'Solo_PeripheralVocals'
                        WHEN 8 THEN 'Solo_PeripheralCymbals'
                        WHEN 6 THEN 'Solo_PeripheralDrums'
                    END AS instrument
                FROM band_member_stats
                WHERE band_type = @bandType
                  AND team_key = ANY(@teamKeys)
                  AND instrument_id IS NOT NULL
            ),
            entry_assignments AS (
                SELECT
                    band_type,
                    team_key,
                    instrument_combo,
                    song_id,
                    string_agg(account_id || '=' || instrument, '|' ORDER BY account_id) AS assignment_key,
                    jsonb_object_agg(account_id, instrument ORDER BY account_id) AS member_assignments_json,
                    COUNT(*)::INT AS member_count
                FROM mapped
                WHERE instrument IS NOT NULL
                GROUP BY band_type, team_key, instrument_combo, song_id
            ),
            configuration_rows AS (
                SELECT
                    band_type,
                    team_key,
                    instrument_combo,
                    assignment_key,
                    member_assignments_json,
                    COUNT(*)::INT AS appearance_count
                FROM entry_assignments
                WHERE member_count = @expectedMembers
                GROUP BY band_type, team_key, instrument_combo, assignment_key, member_assignments_json
            )
            INSERT INTO {BandTeamConfigurationTable} (
                band_type, team_key, instrument_combo, assignment_key,
                appearance_count, member_assignments_json, updated_at)
            SELECT
                band_type,
                team_key,
                instrument_combo,
                assignment_key,
                appearance_count,
                member_assignments_json,
                @updatedAt
            FROM configuration_rows
            ON CONFLICT (band_type, team_key, instrument_combo, assignment_key) DO UPDATE SET
                appearance_count = EXCLUDED.appearance_count,
                member_assignments_json = EXCLUDED.member_assignments_json,
                updated_at = EXCLUDED.updated_at
            """;
        insertCmd.Parameters.AddWithValue("bandType", bandType);
        insertCmd.Parameters.AddWithValue("teamKeys", sortedTeamKeys);
        insertCmd.Parameters.AddWithValue("expectedMembers", expectedMembers);
        insertCmd.Parameters.AddWithValue("updatedAt", DateTime.UtcNow);
        return insertCmd.ExecuteNonQuery();
    }

    private static T ExecuteWithDeadlockRetry<T>(Func<T> action, int maxDeadlockRetries)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                return action();
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DeadlockDetected && attempt < maxDeadlockRetries)
            {
                attempt++;
                System.Threading.Thread.Sleep(TimeSpan.FromMilliseconds(100 * attempt));
            }
        }
    }

    private static void LockBandTeamMembershipRebuild(NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended('band_team_membership_rebuild', 0))";
        cmd.ExecuteNonQuery();
    }

    private static void DeleteBandTeamMembershipForAccount(NpgsqlConnection conn, NpgsqlTransaction tx, string accountId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DELETE FROM {BandTeamMembershipTable} WHERE account_id = @accountId";
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.ExecuteNonQuery();
    }

    private static void DeleteBandTeamMembershipForTeams(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string bandType,
        IReadOnlyCollection<string> teamKeys)
    {
        if (teamKeys.Count == 0)
            return;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DELETE FROM {BandTeamMembershipTable} WHERE band_type = @bandType AND team_key = ANY(@teamKeys)";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("teamKeys", teamKeys.ToArray());
        cmd.ExecuteNonQuery();
    }

    private static List<BandTeamMembershipWriteRow> BuildBandTeamMembershipWriteRows(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        IReadOnlyList<BandTeamMembershipCountRow> countRows)
    {
        if (countRows.Count == 0)
            return [];

        var comboKeys = countRows
            .Select(static row => new BandTeamMembershipComboKey(row.BandType, row.TeamKey, row.InstrumentCombo))
            .Distinct()
            .ToArray();

        using (var dropCmd = conn.CreateCommand())
        {
            dropCmd.Transaction = tx;
            dropCmd.CommandText = "DROP TABLE IF EXISTS _btm_summary_keys";
            dropCmd.ExecuteNonQuery();
        }

        using (var createCmd = conn.CreateCommand())
        {
            createCmd.Transaction = tx;
            createCmd.CommandText = """
                CREATE TEMP TABLE _btm_summary_keys (
                    band_type TEXT,
                    team_key TEXT,
                    instrument_combo TEXT
                ) ON COMMIT DROP
                """;
            createCmd.ExecuteNonQuery();
        }

        using (var writer = conn.BeginBinaryImport(
            "COPY _btm_summary_keys (band_type, team_key, instrument_combo) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var key in comboKeys)
            {
                writer.StartRow();
                writer.Write(key.BandType, NpgsqlDbType.Text);
                writer.Write(key.TeamKey, NpgsqlDbType.Text);
                writer.Write(key.InstrumentCombo, NpgsqlDbType.Text);
            }

            writer.Complete();
        }

        var memberInstrumentsByCombo = new Dictionary<BandTeamMembershipComboKey, Dictionary<string, HashSet<string>>>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT bms.band_type,
                       bms.team_key,
                       bms.instrument_combo,
                       bms.account_id,
                       bms.instrument_id
                FROM band_member_stats bms
                JOIN _btm_summary_keys keys
                  ON keys.band_type = bms.band_type
                 AND keys.team_key = bms.team_key
                 AND keys.instrument_combo = bms.instrument_combo
                ORDER BY bms.band_type, bms.team_key, bms.instrument_combo, bms.account_id, bms.instrument_id
                """;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(4))
                    continue;

                var instrument = BandInstrumentMapping.ToLeaderboardType(reader.GetInt32(4));
                if (string.IsNullOrWhiteSpace(instrument))
                    continue;

                var comboKey = new BandTeamMembershipComboKey(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2));

                if (!memberInstrumentsByCombo.TryGetValue(comboKey, out var memberLookup))
                {
                    memberLookup = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                    memberInstrumentsByCombo[comboKey] = memberLookup;
                }

                var memberAccountId = reader.GetString(3);
                if (!memberLookup.TryGetValue(memberAccountId, out var instruments))
                {
                    instruments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    memberLookup[memberAccountId] = instruments;
                }

                instruments.Add(instrument);
            }
        }

        var now = DateTime.UtcNow;
        var rows = new List<BandTeamMembershipWriteRow>(countRows.Count);
        foreach (var countRow in countRows)
        {
            var comboKey = new BandTeamMembershipComboKey(countRow.BandType, countRow.TeamKey, countRow.InstrumentCombo);
            var memberLookup = SplitTeamKey(countRow.TeamKey)
                .ToDictionary(
                    static accountId => accountId,
                    static _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);

            if (memberInstrumentsByCombo.TryGetValue(comboKey, out var sourceLookup))
            {
                foreach (var (memberAccountId, instruments) in sourceLookup)
                {
                    if (!memberLookup.TryGetValue(memberAccountId, out var memberInstruments))
                    {
                        memberInstruments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        memberLookup[memberAccountId] = memberInstruments;
                    }

                    memberInstruments.UnionWith(instruments);
                }
            }

            var memberInstrumentsJson = JsonSerializer.Serialize(memberLookup.ToDictionary(
                static kvp => kvp.Key,
                static kvp => NormalizeBandInstruments(kvp.Value)));

            rows.Add(new BandTeamMembershipWriteRow(
                countRow.AccountId,
                countRow.BandType,
                countRow.TeamKey,
                countRow.InstrumentCombo,
                countRow.AppearanceCount,
                memberInstrumentsJson,
                now));
        }

        return rows;
    }

    private static void UpsertBandTeamMembershipRows(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        IReadOnlyList<BandTeamMembershipWriteRow> rows)
    {
        if (rows.Count == 0)
            return;

        using (var dropCmd = conn.CreateCommand())
        {
            dropCmd.Transaction = tx;
            dropCmd.CommandText = "DROP TABLE IF EXISTS _btm_summary_staging";
            dropCmd.ExecuteNonQuery();
        }

        using (var createCmd = conn.CreateCommand())
        {
            createCmd.Transaction = tx;
            createCmd.CommandText = """
                CREATE TEMP TABLE _btm_summary_staging (
                    account_id TEXT,
                    band_type TEXT,
                    team_key TEXT,
                    instrument_combo TEXT,
                    appearance_count INT,
                    member_instruments_json JSONB,
                    updated_at TIMESTAMPTZ
                ) ON COMMIT DROP
                """;
            createCmd.ExecuteNonQuery();
        }

        using (var writer = conn.BeginBinaryImport(
            "COPY _btm_summary_staging (account_id, band_type, team_key, instrument_combo, appearance_count, member_instruments_json, updated_at) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var row in rows)
            {
                writer.StartRow();
                writer.Write(row.AccountId, NpgsqlDbType.Text);
                writer.Write(row.BandType, NpgsqlDbType.Text);
                writer.Write(row.TeamKey, NpgsqlDbType.Text);
                writer.Write(row.InstrumentCombo, NpgsqlDbType.Text);
                writer.Write(row.AppearanceCount, NpgsqlDbType.Integer);
                writer.Write(row.MemberInstrumentsJson, NpgsqlDbType.Jsonb);
                writer.Write(row.UpdatedAt, NpgsqlDbType.TimestampTz);
            }

            writer.Complete();
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            INSERT INTO {BandTeamMembershipTable} (account_id, band_type, team_key, instrument_combo, appearance_count, member_instruments_json, updated_at)
            SELECT account_id, band_type, team_key, instrument_combo, appearance_count, member_instruments_json, updated_at
            FROM _btm_summary_staging
            ON CONFLICT (account_id, band_type, team_key, instrument_combo) DO UPDATE SET
                appearance_count = EXCLUDED.appearance_count,
                member_instruments_json = EXCLUDED.member_instruments_json,
                updated_at = EXCLUDED.updated_at
            """;
        cmd.ExecuteNonQuery();
    }

    private static List<string> NormalizeBandInstruments(IEnumerable<string> instruments) =>
        BandComboIds.ToInstruments(BandComboIds.FromInstruments(instruments)).ToList();

    private static List<string> SplitTeamKey(string teamKey) =>
        teamKey
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static accountId => !string.IsNullOrWhiteSpace(accountId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private sealed record BandTeamMembershipCountRow(
        string AccountId,
        string BandType,
        string TeamKey,
        string InstrumentCombo,
        int AppearanceCount);

    private sealed record BandTeamMembershipWriteRow(
        string AccountId,
        string BandType,
        string TeamKey,
        string InstrumentCombo,
        int AppearanceCount,
        string MemberInstrumentsJson,
        DateTime UpdatedAt);

    private sealed record BandTeamMembershipComboKey(string BandType, string TeamKey, string InstrumentCombo);
}

public sealed record BandPruneResult(
    int DeletedEntries,
    int DeletedMemberStats,
    int DeletedMemberLookups,
    IReadOnlyDictionary<string, IReadOnlyCollection<string>> AffectedTeamsByBandType)
{
    public static BandPruneResult Empty { get; } = new(
        0,
        0,
        0,
        new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase));
}
