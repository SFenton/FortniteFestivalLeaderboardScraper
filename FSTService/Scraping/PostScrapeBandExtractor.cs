using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using FSTService.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Scraping;

/// <summary>
/// Post-scrape phase that extracts band leaderboard data from solo
/// leaderboard rows where <c>band_members_json IS NOT NULL</c>.
///
/// Runs entirely in SQL — reads the JSONB column, groups by team key,
/// and upserts into <c>band_entries</c>, <c>band_member_stats</c>, and
/// <c>band_members</c>. Zero channel backpressure, zero async contention,
/// zero impact on the main scrape pipeline.
/// </summary>
public sealed class PostScrapeBandExtractor
{
    private const int BandContextSeedLockNamespace = 1179866191;
    private const int BandContextSeedLockKey = 1;

    private readonly NpgsqlDataSource _dataSource;
    private readonly IPathDataStore _pathDataStore;
    private readonly ScraperOptions _options;
    private readonly bool _useSnapshotOverlayWorkerReaders;
    private readonly ScrapeProgressTracker? _progress;
    private readonly ILogger<PostScrapeBandExtractor> _log;

    public PostScrapeBandExtractor(
        NpgsqlDataSource dataSource,
        IPathDataStore pathDataStore,
        ILogger<PostScrapeBandExtractor> log,
        ScrapeProgressTracker? progress = null,
        IOptions<ScraperOptions>? options = null,
        IOptions<FeatureOptions>? featureOptions = null)
    {
        _dataSource = dataSource;
        _pathDataStore = pathDataStore;
        _options = options?.Value ?? new ScraperOptions();
        _useSnapshotOverlayWorkerReaders = featureOptions?.Value is
        {
            UseSnapshotOverlayWorkerReaders: true,
            UsePublishedScopeSources: false,
        };
        _progress = progress;
        _log = log;
    }

    /// <summary>
    /// Extract band entries from solo leaderboard data and upsert into band tables.
    /// Processes all instruments in a single pass. The rollout candidate reads
    /// finalized snapshots plus overlays; the rollback path reads the legacy
    /// mutable table.
    /// </summary>
    public Task<BandExtractionResult> RunAsync(CancellationToken ct) =>
        RunAsync(snapshotId: null, ct);

    public Task EnsureBandContextReadyAsync(CancellationToken ct) =>
        _useSnapshotOverlayWorkerReaders
            ? EnsureBandContextSeededAsync(ct)
            : Task.CompletedTask;

    public async Task<BandExtractionResult> RunAsync(long? snapshotId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        _log.LogInformation("Post-scrape band extraction starting...");

        int totalBandRows = 0;
        int totalMemberStats = 0;
        int totalMemberLookups = 0;
        var maxDegreeOfParallelism = _options.BandExtractionParallelism > 0
            ? Math.Clamp(_options.BandExtractionParallelism, 1, 64)
            : Math.Clamp(Environment.ProcessorCount, 1, 8);

        if (_useSnapshotOverlayWorkerReaders)
        {
            await EnsureBandContextReadyAsync(ct);
            if (snapshotId is > 0)
                await ReconcileBandContextFromSnapshotAsync(snapshotId.Value, ct);
        }

        long bandContextRowCount = 0;
        var songIds = new List<string>();
        await using (var conn = await _dataSource.OpenConnectionAsync(ct))
        {
            await using var songCmd = conn.CreateCommand();
            songCmd.CommandText = _useSnapshotOverlayWorkerReaders
                ? """
                    SELECT song_id, COUNT(*)::BIGINT
                    FROM leaderboard_band_context
                    GROUP BY song_id
                    ORDER BY song_id
                    """
                : """
                    SELECT song_id, COUNT(*)::BIGINT
                    FROM leaderboard_entries
                    WHERE band_members_json IS NOT NULL
                    GROUP BY song_id
                    ORDER BY song_id
                    """;
            await using var reader = await songCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                songIds.Add(reader.GetString(0));
                bandContextRowCount += reader.GetInt64(1);
            }
        }

        _log.LogInformation(
            "Found {Count:N0} solo entries with band context across {SongCount:N0} song(s) from {Source}.",
            bandContextRowCount,
            songIds.Count,
            _useSnapshotOverlayWorkerReaders ? "snapshot-derived band context" : "legacy leaderboard rows");
        if (bandContextRowCount == 0) return BandExtractionResult.Empty;

        // Process in batches by song_id to limit transaction size
        _log.LogInformation("Extracting band data from {SongCount} songs with up to {Parallelism} concurrent workers.",
            songIds.Count, maxDegreeOfParallelism);
        _progress?.SetSubOperation("extracting_band_context");
        _progress?.BeginPhaseProgress(songIds.Count);

        // Load CHOpt max scores for validation
        var allMaxScores = _pathDataStore.GetAllMaxScores();
        var persistence = new BandLeaderboardPersistence(
            _dataSource,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BandLeaderboardPersistence>.Instance);
        var impactedTeamsByBandType = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>(StringComparer.OrdinalIgnoreCase);
        var impactedCurrentProjectionScopes = new ConcurrentDictionary<BandCurrentProjectionScopeKey, byte>();

        await Parallel.ForEachAsync(songIds,
            new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism, CancellationToken = ct },
            async (songId, innerCt) =>
            {
                try
                {
                    var (bands, members, lookups, impactedTeams, impactedScopes) = await ExtractSongBandDataAsync(songId, allMaxScores, persistence, innerCt);
                    Interlocked.Add(ref totalBandRows, bands);
                    Interlocked.Add(ref totalMemberStats, members);
                    Interlocked.Add(ref totalMemberLookups, lookups);
                    foreach (var (bandType, teamKey) in impactedTeams)
                    {
                        var teams = impactedTeamsByBandType.GetOrAdd(
                            bandType,
                            static _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
                        teams.TryAdd(teamKey, 0);
                    }
                    foreach (var scope in impactedScopes)
                        impactedCurrentProjectionScopes.TryAdd(scope, 0);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Band extraction failed for song {SongId}. Will retry next pass.", songId);
                }
                finally
                {
                    _progress?.ReportPhaseItemComplete();
                }
            });

        RebuildImpactedMembershipSummaries(persistence, impactedTeamsByBandType, ct);

        sw.Stop();
        _log.LogInformation(
            "Post-scrape band extraction complete in {Elapsed}. " +
            "Band entries: {BandRows:N0}, member stats: {MemberStats:N0}, member lookups: {MemberLookups:N0}.",
            sw.Elapsed, totalBandRows, totalMemberStats, totalMemberLookups);

        return new BandExtractionResult(
            totalBandRows,
            totalMemberStats,
            totalMemberLookups,
            impactedTeamsByBandType.ToDictionary(
                static kvp => kvp.Key,
                static kvp => (IReadOnlyCollection<string>)kvp.Value.Keys.ToArray(),
                StringComparer.OrdinalIgnoreCase),
            BandCurrentProjectionScopeTracker.OrderedDistinct(impactedCurrentProjectionScopes.Keys));
    }

    private async Task<(int Bands, int Members, int Lookups, List<(string BandType, string TeamKey)> ImpactedTeams, List<BandCurrentProjectionScopeKey> ImpactedCurrentProjectionScopes)> ExtractSongBandDataAsync(
        string songId,
        IReadOnlyDictionary<string, SongMaxScores> allMaxScores,
        BandLeaderboardPersistence persistence,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Read all band-context rows for this song
        var entries = new List<BandExtractRow>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = _useSnapshotOverlayWorkerReaders
                ? """
                    SELECT account_id, instrument, score, accuracy, is_full_combo, stars, difficulty,
                           season, end_time, band_members_json, band_score, base_score,
                           instrument_bonus, overdrive_bonus, instrument_combo
                    FROM leaderboard_band_context
                    WHERE song_id = @songId
                    """
                : """
                    SELECT account_id, instrument, score, accuracy, is_full_combo, stars, difficulty,
                           season, end_time, band_members_json, band_score, base_score,
                           instrument_bonus, overdrive_bonus, instrument_combo
                    FROM leaderboard_entries
                    WHERE song_id = @songId AND band_members_json IS NOT NULL
                    """;
            cmd.Parameters.AddWithValue("songId", songId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                entries.Add(new BandExtractRow
                {
                    AccountId = reader.GetString(0),
                    Instrument = reader.GetString(1),
                    Score = reader.GetInt32(2),
                    Accuracy = reader.GetInt32(3),
                    IsFullCombo = reader.GetBoolean(4),
                    Stars = reader.GetInt32(5),
                    Difficulty = reader.GetInt32(6),
                    Season = reader.GetInt32(7),
                    EndTime = reader.IsDBNull(8) ? null : reader.GetString(8),
                    BandMembersJson = reader.GetString(9),
                    BandScore = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                    BaseScore = reader.IsDBNull(11) ? null : reader.GetInt32(11),
                    InstrumentBonus = reader.IsDBNull(12) ? null : reader.GetInt32(12),
                    OverdriveBonus = reader.IsDBNull(13) ? null : reader.GetInt32(13),
                    InstrumentCombo = reader.IsDBNull(14) ? null : reader.GetString(14),
                });
            }
        }

        if (entries.Count == 0) return (0, 0, 0, [], []);

        // Build band entries from the stored data
        var bandEntries = new Dictionary<(string BandType, string TeamKey, string Combo), BandLeaderboardEntry>();
        var maxScores = allMaxScores.TryGetValue(songId, out var ms) ? ms : null;

        foreach (var row in entries)
        {
            List<BandMemberStats>? members;
            try
            {
                members = JsonSerializer.Deserialize(row.BandMembersJson,
                    BandMembersJsonContext.Default.ListBandMemberStats);
            }
            catch
            {
                continue; // Skip malformed JSON
            }

            if (members is not { Count: >= 2 }) continue;

            var bandType = members.Count switch
            {
                2 => "Band_Duets",
                3 => "Band_Trios",
                _ => "Band_Quad",
            };

            var sortedIds = members
                .Select(m => m.AccountId)
                .Where(id => !string.IsNullOrEmpty(id))
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (sortedIds.Count < 2) continue;

            var teamKey = string.Join(':', sortedIds);
            var combo = row.InstrumentCombo ?? "";

            var key = (bandType, teamKey, combo);

            // Keep highest score per (bandType, teamKey, combo)
            if (bandEntries.TryGetValue(key, out var existing) && existing.Score >= (row.BandScore ?? row.Score))
                continue;

            var bandEntry = new BandLeaderboardEntry
            {
                TeamKey = teamKey,
                TeamMembers = sortedIds.ToArray(),
                Score = row.BandScore ?? row.Score,
                BaseScore = row.BaseScore,
                InstrumentBonus = row.InstrumentBonus,
                OverdriveBonus = row.OverdriveBonus,
                Accuracy = row.Accuracy,
                IsFullCombo = row.IsFullCombo,
                Stars = row.Stars,
                Difficulty = row.Difficulty,
                Season = row.Season,
                EndTime = row.EndTime,
                Source = "solo_extract",
                InstrumentCombo = combo,
                MemberStats = members,
            };

            // Apply CHOpt validation
            BandScrapePhase.ApplyChOptValidation(bandEntry, maxScores);

            bandEntries[key] = bandEntry;
        }

        if (bandEntries.Count == 0) return (0, 0, 0, [], []);

        // Group by band type and upsert
        int totalBands = 0, totalMembers = 0, totalLookups = 0;
        var impactedTeams = new List<(string BandType, string TeamKey)>();
        var impactedCurrentProjectionScopes = new HashSet<BandCurrentProjectionScopeKey>();

        foreach (var group in bandEntries.GroupBy(kv => kv.Key.BandType))
        {
            var bandType = group.Key;
            var batchEntries = group.Select(kv => kv.Value).ToList();
            impactedTeams.AddRange(batchEntries
                .Select(static entry => entry.TeamKey)
                .Where(static teamKey => !string.IsNullOrWhiteSpace(teamKey))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(teamKey => (bandType, teamKey)));

            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                var (bands, members, lookups) = persistence.UpsertBandEntriesDirect(
                    songId, bandType, batchEntries, conn, tx, rebuildTeamMembership: false);
                await tx.CommitAsync(ct);

                totalBands += bands;
                totalMembers += members;
                totalLookups += lookups;

                if (bands > 0)
                {
                    foreach (var entry in batchEntries)
                        BandCurrentProjectionScopeTracker.AddScopes(impactedCurrentProjectionScopes, songId, bandType, entry.InstrumentCombo);
                }
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        return (totalBands, totalMembers, totalLookups, impactedTeams, BandCurrentProjectionScopeTracker.OrderedDistinct(impactedCurrentProjectionScopes).ToList());
    }

    private async Task EnsureBandContextSeededAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var lockCmd = conn.CreateCommand())
        {
            lockCmd.Transaction = tx;
            lockCmd.CommandText = "SELECT pg_advisory_xact_lock(@namespace, @key)";
            lockCmd.Parameters.AddWithValue("namespace", BandContextSeedLockNamespace);
            lockCmd.Parameters.AddWithValue("key", BandContextSeedLockKey);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        await using (var stateCmd = conn.CreateCommand())
        {
            stateCmd.Transaction = tx;
            stateCmd.CommandText = """
                SELECT seeded_at IS NOT NULL
                FROM leaderboard_band_context_state
                WHERE id = TRUE
                """;
            if (await stateCmd.ExecuteScalarAsync(ct) is true)
            {
                await tx.CommitAsync(ct);
                return;
            }
        }

        long legacySourceRows;
        await using (var legacyCmd = conn.CreateCommand())
        {
            legacyCmd.Transaction = tx;
            legacyCmd.CommandTimeout = 300;
            legacyCmd.CommandText = """
                WITH source_rows AS MATERIALIZED (
                    SELECT legacy.song_id, legacy.instrument, legacy.account_id, legacy.score,
                           legacy.accuracy, legacy.is_full_combo, legacy.stars, legacy.season,
                           legacy.percentile, legacy.source, legacy.difficulty, legacy.end_time,
                           legacy.band_members_json, legacy.band_score, legacy.base_score,
                           legacy.instrument_bonus, legacy.overdrive_bonus, legacy.instrument_combo,
                           legacy.first_seen_at, legacy.last_updated_at
                    FROM leaderboard_entries legacy
                    WHERE legacy.band_members_json IS NOT NULL
                ), inserted AS (
                    INSERT INTO leaderboard_band_context (
                        song_id, instrument, account_id, score, accuracy, is_full_combo,
                        stars, season, percentile, source, difficulty, end_time,
                        band_members_json, band_score, base_score, instrument_bonus,
                        overdrive_bonus, instrument_combo, first_seen_at, last_updated_at)
                    SELECT song_id, instrument, account_id, score, accuracy, is_full_combo,
                           stars, season, percentile, source, difficulty, end_time,
                           band_members_json, band_score, base_score, instrument_bonus,
                           overdrive_bonus, instrument_combo, first_seen_at, last_updated_at
                    FROM source_rows
                    ON CONFLICT (song_id, instrument, account_id) DO NOTHING
                    RETURNING 1
                )
                SELECT COUNT(*)::BIGINT FROM source_rows
                """;
            legacySourceRows = Convert.ToInt64(await legacyCmd.ExecuteScalarAsync(ct));
        }

        long overlaySourceRows;
        await using (var overlayCmd = conn.CreateCommand())
        {
            overlayCmd.Transaction = tx;
            overlayCmd.CommandTimeout = 300;
            overlayCmd.CommandText = """
                WITH source_rows AS MATERIALIZED (
                    SELECT overlay.song_id, overlay.instrument, overlay.account_id, overlay.score,
                           overlay.accuracy, overlay.is_full_combo, overlay.stars, overlay.season,
                           overlay.percentile, overlay.source, overlay.difficulty, overlay.end_time,
                           overlay.band_members_json, overlay.band_score, overlay.base_score,
                           overlay.instrument_bonus, overlay.overdrive_bonus, overlay.instrument_combo,
                           overlay.first_seen_at, overlay.last_updated_at
                    FROM leaderboard_entries_overlay overlay
                    WHERE overlay.band_members_json IS NOT NULL
                ), inserted AS (
                    INSERT INTO leaderboard_band_context (
                        song_id, instrument, account_id, score, accuracy, is_full_combo,
                        stars, season, percentile, source, difficulty, end_time,
                        band_members_json, band_score, base_score, instrument_bonus,
                        overdrive_bonus, instrument_combo, first_seen_at, last_updated_at)
                    SELECT song_id, instrument, account_id, score, accuracy, is_full_combo,
                           stars, season, percentile, source, difficulty, end_time,
                           band_members_json, band_score, base_score, instrument_bonus,
                           overdrive_bonus, instrument_combo, first_seen_at, last_updated_at
                    FROM source_rows
                    ON CONFLICT (song_id, instrument, account_id) DO NOTHING
                    RETURNING 1
                )
                SELECT COUNT(*)::BIGINT FROM source_rows
                """;
            overlaySourceRows = Convert.ToInt64(await overlayCmd.ExecuteScalarAsync(ct));
        }

        long contextRows;
        await using (var countCmd = conn.CreateCommand())
        {
            countCmd.Transaction = tx;
            countCmd.CommandText = "SELECT COUNT(*)::BIGINT FROM leaderboard_band_context";
            contextRows = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct));
        }

        await using (var persistCmd = conn.CreateCommand())
        {
            persistCmd.Transaction = tx;
            persistCmd.CommandText = """
                INSERT INTO leaderboard_band_context_state (
                    id, seeded_at, legacy_source_rows, overlay_source_rows,
                    context_rows, updated_at)
                VALUES (TRUE, @now, @legacySourceRows, @overlaySourceRows, @contextRows, @now)
                ON CONFLICT (id) DO UPDATE SET
                    seeded_at = EXCLUDED.seeded_at,
                    legacy_source_rows = EXCLUDED.legacy_source_rows,
                    overlay_source_rows = EXCLUDED.overlay_source_rows,
                    context_rows = EXCLUDED.context_rows,
                    updated_at = EXCLUDED.updated_at
                """;
            persistCmd.Parameters.AddWithValue("now", DateTime.UtcNow);
            persistCmd.Parameters.AddWithValue("legacySourceRows", legacySourceRows);
            persistCmd.Parameters.AddWithValue("overlaySourceRows", overlaySourceRows);
            persistCmd.Parameters.AddWithValue("contextRows", contextRows);
            await persistCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        _log.LogInformation(
            "Seeded accumulated band context: legacy source {LegacyRows:N0}, overlay source {OverlayRows:N0}, context {ContextRows:N0}.",
            legacySourceRows,
            overlaySourceRows,
            contextRows);
    }

    private async Task ReconcileBandContextFromSnapshotAsync(long snapshotId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = """
            WITH source_rows AS MATERIALIZED (
                SELECT snapshot.song_id, snapshot.instrument, snapshot.account_id,
                       snapshot.score, snapshot.accuracy, snapshot.is_full_combo,
                       snapshot.stars, snapshot.season, snapshot.percentile,
                       snapshot.source, snapshot.difficulty, snapshot.end_time,
                       snapshot.band_members_json, snapshot.band_score,
                       snapshot.base_score, snapshot.instrument_bonus,
                       snapshot.overdrive_bonus, snapshot.instrument_combo,
                       snapshot.last_updated_at
                FROM leaderboard_band_context context
                JOIN leaderboard_entries_snapshot snapshot
                  ON snapshot.snapshot_id = @snapshotId
                 AND snapshot.song_id = context.song_id
                 AND snapshot.instrument = context.instrument
                 AND snapshot.account_id = context.account_id
            )
            UPDATE leaderboard_band_context context
            SET score = CASE WHEN source.score != context.score THEN source.score ELSE context.score END,
                accuracy = CASE WHEN source.score != context.score THEN source.accuracy ELSE context.accuracy END,
                is_full_combo = CASE WHEN source.score != context.score THEN source.is_full_combo ELSE context.is_full_combo END,
                stars = CASE WHEN source.score != context.score THEN source.stars ELSE context.stars END,
                season = CASE WHEN source.score != context.score THEN source.season ELSE context.season END,
                difficulty = CASE
                    WHEN source.difficulty >= 0 AND context.difficulty < 0 THEN source.difficulty
                    WHEN source.score != context.score THEN source.difficulty
                    ELSE context.difficulty
                END,
                percentile = CASE
                    WHEN source.score != context.score THEN source.percentile
                    WHEN source.percentile > 0 AND context.percentile <= 0 THEN source.percentile
                    ELSE context.percentile
                END,
                source = CASE
                    WHEN context.source = 'scrape' THEN 'scrape'
                    WHEN source.source = 'scrape' THEN 'scrape'
                    WHEN context.source = 'backfill' THEN 'backfill'
                    WHEN source.source = 'backfill' THEN 'backfill'
                    ELSE source.source
                END,
                end_time = CASE WHEN source.score != context.score THEN source.end_time ELSE context.end_time END,
                band_members_json = COALESCE(source.band_members_json, context.band_members_json),
                band_score = COALESCE(source.band_score, context.band_score),
                base_score = COALESCE(source.base_score, context.base_score),
                instrument_bonus = COALESCE(source.instrument_bonus, context.instrument_bonus),
                overdrive_bonus = COALESCE(source.overdrive_bonus, context.overdrive_bonus),
                instrument_combo = COALESCE(source.instrument_combo, context.instrument_combo),
                last_updated_at = source.last_updated_at
            FROM source_rows source
            WHERE context.song_id = source.song_id
              AND context.instrument = source.instrument
              AND context.account_id = source.account_id
              AND source.last_updated_at >= context.last_updated_at
              AND (
                  source.score != context.score
                  OR (source.source = 'scrape' AND context.source != 'scrape')
                  OR (source.difficulty >= 0 AND context.difficulty < 0)
                  OR (source.percentile > 0 AND context.percentile <= 0)
                  OR (source.band_members_json IS NOT NULL AND context.band_members_json IS NULL)
                  OR COALESCE(source.base_score, -1) != COALESCE(context.base_score, -1)
                  OR COALESCE(source.overdrive_bonus, -1) != COALESCE(context.overdrive_bonus, -1)
              )
            """;
        cmd.Parameters.AddWithValue("snapshotId", snapshotId);
        var updated = await cmd.ExecuteNonQueryAsync(ct);
        _log.LogInformation(
            "Reconciled {Updated:N0} accumulated band-context row(s) from snapshot {SnapshotId}.",
            updated,
            snapshotId);
    }

    private void RebuildImpactedMembershipSummaries(
        BandLeaderboardPersistence persistence,
        ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> impactedTeamsByBandType,
        CancellationToken ct)
    {
        var batchSize = Math.Clamp(_options.BandMembershipRebuildBatchSize, 1, 10_000);
        var rebuildBatches = impactedTeamsByBandType
            .OrderBy(static kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(kvp => kvp.Value.Keys
                .OrderBy(static teamKey => teamKey, StringComparer.OrdinalIgnoreCase)
                .Chunk(batchSize)
                .Select(teamKeys => (BandType: kvp.Key, TeamKeys: (IReadOnlyCollection<string>)teamKeys)))
            .ToList();

        if (rebuildBatches.Count == 0)
            return;

        _log.LogInformation(
            "Rebuilding band-team membership summaries for {TeamCount:N0} impacted team(s) in {BatchCount:N0} batch(es).",
            impactedTeamsByBandType.Sum(static kvp => kvp.Value.Count),
            rebuildBatches.Count);

        _progress?.SetSubOperation("rebuilding_band_membership_summary");
        _progress?.BeginPhaseProgress(rebuildBatches.Count);

        foreach (var (bandType, teamKeys) in rebuildBatches)
        {
            ct.ThrowIfCancellationRequested();
            persistence.RebuildBandTeamMembershipForTeams(bandType, teamKeys);
            _progress?.ReportPhaseItemComplete();
        }
    }

    private sealed class BandExtractRow
    {
        public string AccountId { get; init; } = "";
        public string Instrument { get; init; } = "";
        public int Score { get; init; }
        public int Accuracy { get; init; }
        public bool IsFullCombo { get; init; }
        public int Stars { get; init; }
        public int Difficulty { get; init; }
        public int Season { get; init; }
        public string? EndTime { get; init; }
        public string BandMembersJson { get; init; } = "";
        public int? BandScore { get; init; }
        public int? BaseScore { get; init; }
        public int? InstrumentBonus { get; init; }
        public int? OverdriveBonus { get; init; }
        public string? InstrumentCombo { get; init; }
    }
}

public sealed record BandExtractionResult(
    int BandRows,
    int MemberStats,
    int MemberLookups,
    IReadOnlyDictionary<string, IReadOnlyCollection<string>> ImpactedTeamsByBandType,
    IReadOnlyCollection<BandCurrentProjectionScopeKey> ImpactedCurrentProjectionScopes)
{
    public static BandExtractionResult Empty { get; } = new(
        0,
        0,
        0,
        new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase),
        []);
}
