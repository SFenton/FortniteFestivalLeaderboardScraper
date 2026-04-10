using System.Diagnostics;
using System.Text.Json;
using FSTService.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Scraping;

/// <summary>
/// Post-scrape phase that extracts band leaderboard data from solo
/// <c>leaderboard_entries</c> rows where <c>band_members_json IS NOT NULL</c>.
///
/// Runs entirely in SQL — reads the JSONB column, groups by team key,
/// and upserts into <c>band_entries</c>, <c>band_member_stats</c>, and
/// <c>band_members</c>. Zero channel backpressure, zero async contention,
/// zero impact on the main scrape pipeline.
/// </summary>
public sealed class PostScrapeBandExtractor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IPathDataStore _pathDataStore;
    private readonly ILogger<PostScrapeBandExtractor> _log;

    public PostScrapeBandExtractor(
        NpgsqlDataSource dataSource,
        IPathDataStore pathDataStore,
        ILogger<PostScrapeBandExtractor> log)
    {
        _dataSource = dataSource;
        _pathDataStore = pathDataStore;
        _log = log;
    }

    /// <summary>
    /// Extract band entries from solo leaderboard data and upsert into band tables.
    /// Processes all instruments in a single pass using the partial index on
    /// <c>band_members_json IS NOT NULL</c>.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        _log.LogInformation("Post-scrape band extraction starting...");

        int totalBandRows = 0;
        int totalMemberStats = 0;
        int totalMemberLookups = 0;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Count rows with band data to estimate work
        await using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = "SELECT COUNT(*) FROM leaderboard_entries WHERE band_members_json IS NOT NULL";
            var count = (long)(await countCmd.ExecuteScalarAsync(ct))!;
            _log.LogInformation("Found {Count:N0} solo entries with band context to extract.", count);
            if (count == 0) return;
        }

        // Process in batches by song_id to limit transaction size
        var songIds = new List<string>();
        await using (var songCmd = conn.CreateCommand())
        {
            songCmd.CommandText = "SELECT DISTINCT song_id FROM leaderboard_entries WHERE band_members_json IS NOT NULL";
            await using var reader = await songCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                songIds.Add(reader.GetString(0));
        }

        _log.LogInformation("Extracting band data from {SongCount} songs.", songIds.Count);

        // Load CHOpt max scores for validation
        var allMaxScores = _pathDataStore.GetAllMaxScores();

        foreach (var songId in songIds)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var (bands, members, lookups) = await ExtractSongBandDataAsync(conn, songId, allMaxScores, ct);
                totalBandRows += bands;
                totalMemberStats += members;
                totalMemberLookups += lookups;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Band extraction failed for song {SongId}. Will retry next pass.", songId);
            }
        }

        sw.Stop();
        _log.LogInformation(
            "Post-scrape band extraction complete in {Elapsed}. " +
            "Band entries: {BandRows:N0}, member stats: {MemberStats:N0}, member lookups: {MemberLookups:N0}.",
            sw.Elapsed, totalBandRows, totalMemberStats, totalMemberLookups);
    }

    private async Task<(int Bands, int Members, int Lookups)> ExtractSongBandDataAsync(
        NpgsqlConnection conn, string songId,
        IReadOnlyDictionary<string, SongMaxScores> allMaxScores,
        CancellationToken ct)
    {
        // Read all band-context rows for this song
        var entries = new List<BandExtractRow>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
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

        if (entries.Count == 0) return (0, 0, 0);

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

        if (bandEntries.Count == 0) return (0, 0, 0);

        // Group by band type and upsert
        int totalBands = 0, totalMembers = 0, totalLookups = 0;

        foreach (var group in bandEntries.GroupBy(kv => kv.Key.BandType))
        {
            var bandType = group.Key;
            var batchEntries = group.Select(kv => kv.Value).ToList();

            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                var persistence = new BandLeaderboardPersistence(_dataSource,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<BandLeaderboardPersistence>.Instance);
                var (bands, members, lookups) = persistence.UpsertBandEntriesDirect(
                    songId, bandType, batchEntries, conn, tx);
                await tx.CommitAsync(ct);

                totalBands += bands;
                totalMembers += members;
                totalLookups += lookups;
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        return (totalBands, totalMembers, totalLookups);
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
