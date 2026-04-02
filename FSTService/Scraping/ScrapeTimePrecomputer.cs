using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// Precomputes JSON responses for registered players and popular leaderboard pages
/// during post-scrape, so API requests can be served from memory in &lt;1ms.
/// </summary>
public sealed class ScrapeTimePrecomputer
{
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly IMetaDatabase _metaDb;
    private readonly IPathDataStore _pathStore;
    private readonly ScrapeProgressTracker _progress;
    private readonly ILogger<ScrapeTimePrecomputer> _log;
    private readonly JsonSerializerOptions _jsonOpts;

    private readonly ConcurrentDictionary<string, PrecomputedResponse> _store = new(StringComparer.Ordinal);

    /// <summary>
    /// Population tiers per (songId, instrument). Set during precomputation,
    /// consumed by SongEndpoints to enrich the /api/songs response.
    /// </summary>
    private volatile IReadOnlyDictionary<(string SongId, string Instrument), PopulationTierData>? _populationTiers;

    public ScrapeTimePrecomputer(
        GlobalLeaderboardPersistence persistence,
        IMetaDatabase metaDb,
        IPathDataStore pathStore,
        ScrapeProgressTracker progress,
        ILogger<ScrapeTimePrecomputer> log,
        JsonSerializerOptions jsonOpts)
    {
        _persistence = persistence;
        _metaDb = metaDb;
        _pathStore = pathStore;
        _progress = progress;
        _log = log;
        _jsonOpts = jsonOpts;
    }

    /// <summary>Returns a precomputed response if available, else null.</summary>
    public (byte[] Json, string ETag)? TryGet(string cacheKey)
    {
        if (_store.TryGetValue(cacheKey, out var entry))
            return (entry.Json, entry.ETag);
        return null;
    }

    /// <summary>Gets the precomputed population tier data for the songs endpoint.</summary>
    public IReadOnlyDictionary<(string SongId, string Instrument), PopulationTierData>? GetPopulationTiers()
        => _populationTiers;

    /// <summary>Clears all precomputed data. Called at scrape start.</summary>
    public void InvalidateAll()
    {
        _store.Clear();
        _populationTiers = null;
    }

    public int Count => _store.Count;

    /// <summary>
    /// Precompute all data: player profiles, leaderboard-all pages, and population tiers.
    /// Called after post-scrape enrichment is complete (ranks, backfill, rivals all done).
    /// </summary>
    public async Task PrecomputeAllAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var allMaxScores = _pathStore.GetAllMaxScores();
        var unfilteredPopulation = _metaDb.GetAllLeaderboardPopulation();
        var registeredIds = _metaDb.GetRegisteredAccountIds();
        var instrumentKeys = _persistence.GetInstrumentKeys();

        // ── Phase 1: Population tiers ────────────────────────────
        _progress.SetSubOperation("population_tiers");
        var tiers = ComputePopulationTiers(allMaxScores, instrumentKeys);
        _populationTiers = tiers;
        _log.LogInformation("Precomputed population tiers for {Count} (song, instrument) pairs in {Elapsed}ms.",
            tiers.Count, sw.ElapsedMilliseconds);

        // ── Phase 2: Player profiles (parallel) ─────────────────
        _progress.SetSubOperation("player_profiles");
        var bandScoresCache = BuildBandScoresCache(allMaxScores, instrumentKeys);
        await PrecomputePlayersAsync(registeredIds, allMaxScores, unfilteredPopulation,
            tiers, bandScoresCache, ct);

        // ── Phase 3: Leaderboard-all pages ──────────────────────
        _progress.SetSubOperation("leaderboard_pages");
        PrecomputeLeaderboardAll(allMaxScores, unfilteredPopulation, instrumentKeys);

        // ── Phase 4: Player sub-resources (stats, history, rivals, etc.) ──
        _progress.SetSubOperation("player_sub_resources");
        await PrecomputePlayerSubResourcesAsync(registeredIds, instrumentKeys, ct);

        // ── Phase 5: Rankings pages (page 1 per instrument × metric) ──
        _progress.SetSubOperation("rankings_pages");
        PrecomputeRankingsPages(instrumentKeys);

        // ── Phase 6: Neighborhoods (registered users) ────────────
        _progress.SetSubOperation("neighborhoods");
        PrecomputeNeighborhoods(registeredIds, instrumentKeys);

        // ── Phase 7: Static data (firstseen) ────────────────────
        _progress.SetSubOperation("static_data");
        PrecomputeFirstSeen();

        // ── Cleanup: evict player entries for unregistered accounts ─
        var registeredSet = new HashSet<string>(registeredIds, StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var key in _store.Keys)
        {
            if (key.StartsWith("player:", StringComparison.Ordinal)
                && !registeredSet.Contains(ExtractAccountId(key)))
            {
                _store.TryRemove(key, out _);
                removed++;
            }
        }
        if (removed > 0)
            _log.LogInformation("Evicted {Count} stale precomputed player entries.", removed);

        sw.Stop();
        _log.LogInformation("Scrape-time precomputation complete: {PlayerCount} players, {LbCount} leaderboard-all pages in {Elapsed}s.",
            registeredIds.Count, _store.Count - registeredIds.Count, sw.Elapsed.TotalSeconds);
    }

    private static string ExtractAccountId(string cacheKey)
    {
        // "player:{accountId}:::" → extract accountId
        if (!cacheKey.StartsWith("player:", StringComparison.Ordinal)) return string.Empty;
        var end = cacheKey.IndexOf(':', 7);
        return end < 0 ? cacheKey[7..] : cacheKey[7..end];
    }

    /// <summary>
    /// Precompute a single player (e.g., after /track registration between scrapes).
    /// Covers profile + all sub-resources (stats, history, sync-status, rivals, lb-rivals).
    /// </summary>
    public void PrecomputeUser(string accountId)
    {
        var allMaxScores = _pathStore.GetAllMaxScores();
        var unfilteredPopulation = _metaDb.GetAllLeaderboardPopulation();
        var instrumentKeys = _persistence.GetInstrumentKeys();
        var tiers = _populationTiers ?? ComputePopulationTiers(allMaxScores, instrumentKeys);
        var bandScoresCache = BuildBandScoresCache(allMaxScores, instrumentKeys);
        PrecomputeSinglePlayer(accountId, allMaxScores, unfilteredPopulation, tiers, bandScoresCache);

        // Sub-resources
        var displayNames = _metaDb.GetDisplayNames(new[] { accountId });
        PrecomputePlayerStats(accountId);
        PrecomputePlayerHistory(accountId);
        PrecomputePlayerSyncStatus(accountId);
        PrecomputePlayerRivalsOverview(accountId);
        PrecomputePlayerRivalsAll(accountId, displayNames);
        PrecomputePlayerLeaderboardRivals(accountId, instrumentKeys, displayNames);
    }

    // ═══════════════════════════════════════════════════════════════
    // Population Tiers
    // ═══════════════════════════════════════════════════════════════

    private Dictionary<(string, string), PopulationTierData> ComputePopulationTiers(
        Dictionary<string, SongMaxScores> allMaxScores,
        IReadOnlyList<string> instrumentKeys)
    {
        var result = new ConcurrentDictionary<(string, string), PopulationTierData>();

        // Build flat list of (songId, instrument, maxScore) to process
        var workItems = new List<(string SongId, string Instrument, int MaxScore)>();
        foreach (var (songId, ms) in allMaxScores)
        {
            foreach (var inst in instrumentKeys)
            {
                var max = ms.GetByInstrument(inst);
                if (max.HasValue && max.Value > 0)
                    workItems.Add((songId, inst, max.Value));
            }
        }

        Parallel.ForEach(workItems, new ParallelOptions { MaxDegreeOfParallelism = 8 }, item =>
        {
            var (songId, instrument, maxScore) = item;
            var db = _persistence.GetOrCreateInstrumentDb(instrument);
            var lowerBound = (int)(maxScore * 0.95);
            var upperBound = (int)(maxScore * 1.05);

            var baseCount = db.GetPopulationAtOrBelow(songId, lowerBound);
            var bandScores = db.GetScoresInBand(songId, lowerBound, upperBound);

            // Build changepoints: each score maps to a leeway percentage
            var tiers = new List<PopulationTier>();
            int cumulative = baseCount;
            double prevLeeway = double.NegativeInfinity;
            foreach (var score in bandScores)
            {
                cumulative++;
                double leeway = Math.Round(((double)score / maxScore - 1.0) * 100.0, 1);
                // Only emit a new tier when leeway actually changes
                if (leeway > prevLeeway)
                {
                    tiers.Add(new PopulationTier { Leeway = leeway, Total = cumulative });
                    prevLeeway = leeway;
                }
                else if (tiers.Count > 0)
                {
                    // Same leeway bucket — update the last tier's total
                    tiers[^1] = tiers[^1] with { Total = cumulative };
                }
            }

            result[(songId, instrument)] = new PopulationTierData
            {
                BaseCount = baseCount,
                Tiers = tiers,
            };
        });

        return new Dictionary<(string, string), PopulationTierData>(result);
    }

    // ═══════════════════════════════════════════════════════════════
    // Band Scores Cache (shared across player precomputation)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Pre-fetches all scores in the threshold band per (songId, instrument).
    /// Reused across all player precomputations to avoid redundant DB queries.
    /// </summary>
    private Dictionary<(string, string), int[]> BuildBandScoresCache(
        Dictionary<string, SongMaxScores> allMaxScores,
        IReadOnlyList<string> instrumentKeys)
    {
        var cache = new ConcurrentDictionary<(string, string), int[]>();
        var workItems = new List<(string SongId, string Instrument, int MaxScore)>();
        foreach (var (songId, ms) in allMaxScores)
            foreach (var inst in instrumentKeys)
            {
                var max = ms.GetByInstrument(inst);
                if (max.HasValue && max.Value > 0) workItems.Add((songId, inst, max.Value));
            }

        Parallel.ForEach(workItems, new ParallelOptions { MaxDegreeOfParallelism = 8 }, item =>
        {
            var db = _persistence.GetOrCreateInstrumentDb(item.Instrument);
            var lo = (int)(item.MaxScore * 0.95);
            var hi = (int)(item.MaxScore * 1.05);
            var scores = db.GetScoresInBand(item.SongId, lo, hi);
            cache[(item.SongId, item.Instrument)] = scores.ToArray();
        });

        return new Dictionary<(string, string), int[]>(cache);
    }

    // ═══════════════════════════════════════════════════════════════
    // Player Precomputation
    // ═══════════════════════════════════════════════════════════════

    private async Task PrecomputePlayersAsync(
        HashSet<string> registeredIds,
        Dictionary<string, SongMaxScores> allMaxScores,
        Dictionary<(string SongId, string Instrument), long> unfilteredPopulation,
        IReadOnlyDictionary<(string, string), PopulationTierData> populationTiers,
        Dictionary<(string, string), int[]> bandScoresCache,
        CancellationToken ct)
    {
        if (registeredIds.Count == 0) return;

        // Bulk-resolve display names for all registered users
        var displayNames = _metaDb.GetDisplayNames(registeredIds);

        await Parallel.ForEachAsync(registeredIds, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            (accountId, _) =>
            {
                try
                {
                    PrecomputeSinglePlayer(accountId, allMaxScores, unfilteredPopulation,
                        populationTiers, bandScoresCache, displayNames);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to precompute player {AccountId}", accountId);
                }
                return ValueTask.CompletedTask;
            });
    }

    internal void PrecomputeSinglePlayer(
        string accountId,
        Dictionary<string, SongMaxScores> allMaxScores,
        Dictionary<(string SongId, string Instrument), long> unfilteredPopulation,
        IReadOnlyDictionary<(string, string), PopulationTierData> populationTiers,
        Dictionary<(string, string), int[]> bandScoresCache,
        Dictionary<string, string>? displayNames = null)
    {
        var scores = _persistence.GetPlayerProfile(accountId);
        if (scores.Count == 0) return;

        displayNames ??= _metaDb.GetDisplayNames(new[] { accountId });
        var displayName = displayNames.GetValueOrDefault(accountId);

        // Use stored rank column (no CTE) for base rankings
        var storedRankings = _persistence.GetPlayerStoredRankings(accountId);

        // Build max-threshold map for all songs at leeway=5% (max slider)
        var maxThresholds = new Dictionary<(string SongId, string Instrument), int>();
        foreach (var s in scores)
        {
            if (!allMaxScores.TryGetValue(s.SongId, out var ms)) continue;
            var max = ms.GetByInstrument(s.Instrument);
            if (!max.HasValue) continue;
            maxThresholds[(s.SongId, s.Instrument)] = (int)(max.Value * 1.05);
        }

        // Get all valid historical scores for songs that might be invalid at some leeway
        var allTiers = maxThresholds.Count > 0
            ? _metaDb.GetAllValidScoreTiers(accountId, maxThresholds)
            : new Dictionary<(string, string), List<ValidScoreFallback>>();

        var enriched = new List<PrecomputedPlayerScore>(scores.Count);
        foreach (var s in scores)
        {
            var key = (s.SongId, s.Instrument);
            var storedRank = storedRankings.GetValueOrDefault(key);
            var rank = s.ApiRank > 0 ? s.ApiRank : (storedRank.Rank > 0 ? storedRank.Rank : s.Rank);
            var totalEntries = unfilteredPopulation.TryGetValue(key, out var pop) && pop > 0 ? (int)pop : 0;

            // Compute minLeeway for the current score
            double? minLeeway = null;
            if (allMaxScores.TryGetValue(s.SongId, out var songMax))
            {
                var max = songMax.GetByInstrument(s.Instrument);
                if (max.HasValue && max.Value > 0)
                    minLeeway = Math.Round(((double)s.Score / max.Value - 1.0) * 100.0, 1);
            }

            // Build validScores with rankTiers for this entry
            List<PrecomputedValidScore>? validScores = null;
            if (allTiers.TryGetValue(key, out var historicalScores) && historicalScores.Count > 0
                && allMaxScores.TryGetValue(s.SongId, out var sm))
            {
                var maxVal = sm.GetByInstrument(s.Instrument);
                if (maxVal.HasValue && maxVal.Value > 0)
                {
                    var bandScores = bandScoresCache.GetValueOrDefault(key);
                    validScores = new List<PrecomputedValidScore>();
                    foreach (var fb in historicalScores)
                    {
                        // Skip the current score (it's already the primary entry)
                        if (fb.Score == s.Score) continue;

                        var fbLeeway = Math.Round(((double)fb.Score / maxVal.Value - 1.0) * 100.0, 1);
                        var rankTiers = ComputeRankTiers(fb.Score, maxVal.Value, bandScores, key, s.Instrument);

                        validScores.Add(new PrecomputedValidScore
                        {
                            Score = fb.Score,
                            Accuracy = fb.Accuracy / 1000,
                            IsFullCombo = fb.IsFullCombo,
                            Stars = fb.Stars,
                            MinLeeway = fbLeeway,
                            RankTiers = rankTiers,
                        });
                    }

                    // Remove duplicates and ensure sorted by score desc
                    if (validScores.Count == 0) validScores = null;
                }
            }

            enriched.Add(new PrecomputedPlayerScore
            {
                SongId = s.SongId,
                Instrument = ComboIds.FromInstruments(new[] { s.Instrument }),
                Score = s.Score,
                Accuracy = s.Accuracy / 1000,
                IsFullCombo = s.IsFullCombo,
                Stars = s.Stars,
                Difficulty = s.Difficulty,
                Season = s.Season,
                Percentile = s.Percentile,
                Rank = rank,
                EndTime = s.EndTime,
                TotalEntries = totalEntries,
                MinLeeway = minLeeway,
                ValidScores = validScores,
            });
        }

        var payload = new
        {
            accountId,
            displayName,
            totalScores = enriched.Count,
            scores = enriched,
        };

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOpts);
        var cacheKey = $"player:{accountId}:::";
        Store(cacheKey, jsonBytes);
    }

    /// <summary>
    /// Compute rank tiers (changepoints) for a specific fallback score.
    /// Uses the pre-fetched band scores to avoid DB queries.
    /// </summary>
    private List<RankTier>? ComputeRankTiers(int fallbackScore, int maxScore,
        int[]? bandScores, (string SongId, string Instrument) key, string instrument)
    {
        if (bandScores is null || bandScores.Length == 0)
        {
            // No scores in the threshold band — rank is just basePopulation-based
            var db = _persistence.GetOrCreateInstrumentDb(instrument);
            var baseRank = db.GetRankForScore(key.SongId, fallbackScore);
            return [new RankTier { Leeway = -50, Rank = baseRank }];
        }

        // The band scores are sorted ascending. We need to compute:
        // At each leeway threshold T, rank = (entries above fallbackScore with score <= T) + baseRank
        // baseRank = COUNT(entries with score > fallbackScore AND score <= 0.95*maxScore) + 1
        var lowerBound = (int)(maxScore * 0.95);
        var db2 = _persistence.GetOrCreateInstrumentDb(instrument);
        // Count entries with score > fallbackScore that are always valid (below band)
        int alwaysAbove;
        if (fallbackScore <= lowerBound)
        {
            // fallbackScore is in the always-valid zone
            alwaysAbove = db2.GetRankForScore(key.SongId, fallbackScore) - 1;
        }
        else
        {
            // Count entries above fallbackScore but at or below lowerBound
            alwaysAbove = db2.GetPopulationAtOrBelow(key.SongId, lowerBound) -
                          db2.GetPopulationAtOrBelow(key.SongId, fallbackScore);
            if (alwaysAbove < 0) alwaysAbove = 0;
        }

        var tiers = new List<RankTier>();
        int cumAboveFallback = alwaysAbove;
        double prevLeeway = double.NegativeInfinity;
        int prevRank = -1;

        // At leeway = -5.0 (i.e. -5.0%, everything below 0.95*max), rank is alwaysAbove + 1
        int baseRankForTier = alwaysAbove + 1;
        tiers.Add(new RankTier { Leeway = -5.0, Rank = baseRankForTier });
        prevRank = baseRankForTier;
        prevLeeway = -5.0;

        foreach (var score in bandScores)
        {
            double leeway = Math.Round(((double)score / maxScore - 1.0) * 100.0, 1);
            if (score > fallbackScore)
            {
                cumAboveFallback++;
                int rank = cumAboveFallback + 1;
                if (rank != prevRank && leeway > prevLeeway)
                {
                    tiers.Add(new RankTier { Leeway = leeway, Rank = rank });
                    prevRank = rank;
                    prevLeeway = leeway;
                }
                else if (tiers.Count > 0 && rank != prevRank)
                {
                    tiers[^1] = tiers[^1] with { Leeway = leeway, Rank = rank };
                    prevRank = rank;
                }
            }
        }

        return tiers.Count > 0 ? tiers : null;
    }

    // ═══════════════════════════════════════════════════════════════
    // Leaderboard-all Precomputation
    // ═══════════════════════════════════════════════════════════════

    private void PrecomputeLeaderboardAll(
        Dictionary<string, SongMaxScores> allMaxScores,
        Dictionary<(string SongId, string Instrument), long> unfilteredPopulation,
        IReadOnlyList<string> instrumentKeys)
    {
        // Get all song IDs that have leaderboard data
        var allSongIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var inst in instrumentKeys)
        {
            var db = _persistence.GetOrCreateInstrumentDb(inst);
            var songId = db.GetAnySongId();
            if (songId is not null)
            {
                var counts = db.GetAllSongCounts();
                foreach (var sid in counts.Keys) allSongIds.Add(sid);
            }
        }

        Parallel.ForEach(allSongIds, new ParallelOptions { MaxDegreeOfParallelism = 4 }, songId =>
        {
            try
            {
                // No-leeway variant
                PrecomputeLeaderboardAllForSong(songId, null, allMaxScores, unfilteredPopulation, instrumentKeys);
                // Leeway=1 variant
                PrecomputeLeaderboardAllForSong(songId, 1.0, allMaxScores, unfilteredPopulation, instrumentKeys);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to precompute leaderboard-all for song {SongId}", songId);
            }
        });
    }

    private void PrecomputeLeaderboardAllForSong(
        string songId, double? leeway,
        Dictionary<string, SongMaxScores> allMaxScores,
        Dictionary<(string SongId, string Instrument), long> unfilteredPopulation,
        IReadOnlyList<string> instrumentKeys)
    {
        var instrumentArr = instrumentKeys.ToArray();
        var rawResults = new (string Instrument, List<LeaderboardEntryDto> Entries, int DbCount, int TotalEntries)?[instrumentArr.Length];

        Parallel.For(0, instrumentArr.Length, i =>
        {
            var instrument = instrumentArr[i];
            int? maxScore = null;
            if (leeway.HasValue && allMaxScores.TryGetValue(songId, out var ms))
            {
                var raw = ms.GetByInstrument(instrument);
                if (raw.HasValue) maxScore = (int)(raw.Value * (1.0 + leeway.Value / 100.0));
            }
            var result = _persistence.GetLeaderboardWithCount(songId, instrument, 10, maxScore: maxScore);
            if (result is null) return;

            var (entries, dbCount) = result.Value;
            var popKey = (songId, instrument);
            var totalEntries = Math.Max(
                unfilteredPopulation.TryGetValue(popKey, out var pop) && pop > 0 ? (int)pop : 0,
                dbCount);

            rawResults[i] = (instrument, entries, dbCount, totalEntries);
        });

        var allAccountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rawInstruments = new List<(string Instrument, List<LeaderboardEntryDto> Entries, int DbCount, int TotalEntries)>();
        foreach (var r in rawResults)
        {
            if (r is null) continue;
            var val = r.Value;
            foreach (var e in val.Entries) allAccountIds.Add(e.AccountId);
            rawInstruments.Add(val);
        }

        var names = _metaDb.GetDisplayNames(allAccountIds);

        var instruments = rawInstruments.Select(ri => new
        {
            instrument = ri.Instrument,
            count = ri.Entries.Count,
            totalEntries = ri.TotalEntries,
            localEntries = ri.DbCount,
            entries = ri.Entries.Select(e => new
            {
                e.AccountId,
                DisplayName = names.GetValueOrDefault(e.AccountId),
                e.Score,
                Rank = e.ApiRank > 0 ? e.ApiRank : e.Rank,
                e.Accuracy,
                e.IsFullCombo,
                e.Stars,
                e.Difficulty,
                e.Season,
                e.Percentile,
                e.EndTime,
            }).ToList(),
        }).ToList();

        var payload = new { songId, instruments };
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOpts);
        var cacheKey = $"lb:{songId}:10:{leeway}";
        Store(cacheKey, jsonBytes);
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private void Store(string cacheKey, byte[] json)
    {
        var hash = SHA256.HashData(json);
        var etag = $"\"{Convert.ToBase64String(hash, 0, 16)}\"";
        _store[cacheKey] = new PrecomputedResponse(json, etag);
    }

    // ═══════════════════════════════════════════════════════════════
    // Phase 4: Player Sub-Resources (stats, history, sync-status, rivals, lb-rivals)
    // ═══════════════════════════════════════════════════════════════

    private async Task PrecomputePlayerSubResourcesAsync(
        HashSet<string> registeredIds,
        IReadOnlyList<string> instrumentKeys,
        CancellationToken ct)
    {
        if (registeredIds.Count == 0) return;

        var displayNames = _metaDb.GetDisplayNames(registeredIds);

        await Parallel.ForEachAsync(registeredIds, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            (accountId, _) =>
            {
                try
                {
                    PrecomputePlayerStats(accountId);
                    PrecomputePlayerHistory(accountId);
                    PrecomputePlayerSyncStatus(accountId);
                    PrecomputePlayerRivalsOverview(accountId);
                    PrecomputePlayerRivalsAll(accountId, displayNames);
                    PrecomputePlayerLeaderboardRivals(accountId, instrumentKeys, displayNames);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to precompute sub-resources for {AccountId}", accountId);
                }
                return ValueTask.CompletedTask;
            });
    }

    private void PrecomputePlayerStats(string accountId)
    {
        var tierRows = _metaDb.GetPlayerStatsTiers(accountId);
        if (tierRows.Count == 0) return;

        int totalSongs = _persistence.GetTotalSongCount();
        var payload = new
        {
            accountId,
            totalSongs,
            instruments = tierRows.Select(r => new
            {
                instrument = r.Instrument,
                tiers = JsonSerializer.Deserialize<JsonElement>(r.TiersJson),
            }).ToList(),
        };
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOpts);
        Store($"playerstats:{accountId}", jsonBytes);
    }

    private void PrecomputePlayerHistory(string accountId)
    {
        var history = _metaDb.GetScoreHistory(accountId, 50000);
        var payload = new
        {
            accountId,
            count = history.Count,
            history,
        };
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOpts);
        Store($"history:{accountId}", jsonBytes);
    }

    private void PrecomputePlayerSyncStatus(string accountId)
    {
        var backfill = _metaDb.GetBackfillStatus(accountId);
        var historyRecon = _metaDb.GetHistoryReconStatus(accountId);
        var rivals = _metaDb.GetRivalsStatus(accountId);

        var payload = new
        {
            accountId,
            isTracked = true,
            backfill = backfill is null ? null : new
            {
                status = backfill.Status,
                songsChecked = backfill.SongsChecked,
                totalSongsToCheck = backfill.TotalSongsToCheck,
                entriesFound = backfill.EntriesFound,
                startedAt = backfill.StartedAt,
                completedAt = backfill.CompletedAt,
            },
            historyRecon = historyRecon is null ? null : new
            {
                status = historyRecon.Status,
                songsProcessed = historyRecon.SongsProcessed,
                totalSongsToProcess = historyRecon.TotalSongsToProcess,
                seasonsQueried = historyRecon.SeasonsQueried,
                historyEntriesFound = historyRecon.HistoryEntriesFound,
                startedAt = historyRecon.StartedAt,
                completedAt = historyRecon.CompletedAt,
            },
            rivals = rivals is null ? null : new
            {
                status = rivals.Status,
                combosComputed = rivals.CombosComputed,
                totalCombosToCompute = rivals.TotalCombosToCompute,
                rivalsFound = rivals.RivalsFound,
                startedAt = rivals.StartedAt,
                completedAt = rivals.CompletedAt,
            },
        };
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOpts);
        Store($"syncstatus:{accountId}", jsonBytes);
    }

    private void PrecomputePlayerRivalsOverview(string accountId)
    {
        var status = _metaDb.GetRivalsStatus(accountId);
        var combos = _metaDb.GetRivalCombos(accountId);
        if (combos.Count == 0) return;

        var payload = new
        {
            accountId,
            computedAt = status?.CompletedAt,
            combos = combos.Select(c => new
            {
                combo = c.InstrumentCombo,
                aboveCount = c.AboveCount,
                belowCount = c.BelowCount,
            }).ToList(),
        };
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOpts);
        Store($"rivals-overview:{accountId}", jsonBytes);
    }

    private void PrecomputePlayerRivalsAll(string accountId, Dictionary<string, string> displayNames)
    {
        var combos = _metaDb.GetRivalCombos(accountId);
        if (combos.Count == 0) return;

        var allRivalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var comboData = new Dictionary<string, (List<UserRivalRow> Above, List<UserRivalRow> Below)>();
        foreach (var c in combos)
        {
            var above = _metaDb.GetUserRivals(accountId, c.InstrumentCombo, "above");
            var below = _metaDb.GetUserRivals(accountId, c.InstrumentCombo, "below");
            comboData[c.InstrumentCombo] = (above, below);
            foreach (var r in above) allRivalIds.Add(r.RivalAccountId);
            foreach (var r in below) allRivalIds.Add(r.RivalAccountId);
        }

        var rivalNames = _metaDb.GetDisplayNames(allRivalIds);
        // Merge with provided display names
        foreach (var kv in displayNames)
            rivalNames.TryAdd(kv.Key, kv.Value);

        // Bulk-fetch all song samples for this user (1 query instead of N×6)
        var allSamples = _metaDb.GetAllRivalSongSamplesForUser(accountId);

        // Build deduplicated song index
        var songIndex = new List<string>();
        var songIndexLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        int GetOrAddSongIndex(string songId)
        {
            if (!songIndexLookup.TryGetValue(songId, out var idx))
            {
                idx = songIndex.Count;
                songIndex.Add(songId);
                songIndexLookup[songId] = idx;
            }
            return idx;
        }

        // Pre-index all song IDs from samples
        foreach (var (_, samples) in allSamples)
            foreach (var s in samples)
                GetOrAddSongIndex(s.SongId);

        object MapRivalWithSamples(UserRivalRow r)
        {
            var samples = allSamples.TryGetValue(r.RivalAccountId, out var list)
                ? (object)list.Select(s => new
                {
                    s = GetOrAddSongIndex(s.SongId),
                    i = s.Instrument,
                    ur = s.UserRank,
                    rr = s.RivalRank,
                    us = s.UserScore,
                    rs = s.RivalScore,
                }).ToList()
                : Array.Empty<object>();

            return new
            {
                accountId = r.RivalAccountId,
                displayName = rivalNames.GetValueOrDefault(r.RivalAccountId),
                direction = r.Direction,
                sharedSongCount = r.SharedSongCount,
                aheadCount = r.AheadCount,
                behindCount = r.BehindCount,
                rivalScore = r.RivalScore,
                samples,
            };
        }

        var payload = new
        {
            accountId,
            songs = songIndex,
            combos = comboData.Select(kv => new
            {
                combo = kv.Key,
                above = kv.Value.Above.Select(MapRivalWithSamples).ToList(),
                below = kv.Value.Below.Select(MapRivalWithSamples).ToList(),
            }).ToList(),
        };
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOpts);
        Store($"rivals-all:{accountId}", jsonBytes);
    }

    private void PrecomputePlayerLeaderboardRivals(
        string accountId,
        IReadOnlyList<string> instrumentKeys,
        Dictionary<string, string> displayNames)
    {
        foreach (var instrument in instrumentKeys)
        {
            var rivals = _metaDb.GetLeaderboardRivals(accountId, instrument, "totalscore");
            if (rivals.Count == 0) continue;

            var rivalNames = _metaDb.GetDisplayNames(rivals.Select(r => r.RivalAccountId));
            foreach (var kv in displayNames)
                rivalNames.TryAdd(kv.Key, kv.Value);

            int? userRank = null;
            var db = _persistence.GetOrCreateInstrumentDb(instrument);
            {
                var (_, self, _) = db.GetAccountRankingNeighborhood(accountId, 0, "totalscore");
                if (self is not null)
                    userRank = InstrumentDatabase.GetRankValue(self, "totalscore");
            }

            var above = rivals.Where(r => r.Direction == "above").Select(r => MapLbRival(r, rivalNames));
            var below = rivals.Where(r => r.Direction == "below").Select(r => MapLbRival(r, rivalNames));

            var payload = new
            {
                instrument,
                rankBy = "totalscore",
                userRank,
                above = above.ToList(),
                below = below.ToList(),
            };
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOpts);
            Store($"lb-rivals:{accountId}:{instrument}:totalscore", jsonBytes);
        }
    }

    private static object MapRivalSummary(UserRivalRow r, Dictionary<string, string> names)
    {
        return new
        {
            accountId = r.RivalAccountId,
            displayName = names.GetValueOrDefault(r.RivalAccountId),
            direction = r.Direction,
            sharedSongCount = r.SharedSongCount,
            aheadCount = r.AheadCount,
            behindCount = r.BehindCount,
            rivalScore = r.RivalScore,
        };
    }

    private static object MapLbRival(LeaderboardRivalRow r, Dictionary<string, string> names)
    {
        return new
        {
            accountId = r.RivalAccountId,
            displayName = names.GetValueOrDefault(r.RivalAccountId),
            sharedSongCount = r.SharedSongCount,
            aheadCount = r.AheadCount,
            behindCount = r.BehindCount,
            avgSignedDelta = r.AvgSignedDelta,
            leaderboardRank = r.RivalRank,
            userLeaderboardRank = r.UserRank,
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // Phase 5: Rankings Pages (page 1 for each instrument × metric)
    // ═══════════════════════════════════════════════════════════════

    private static readonly string[] RankingMetrics = ["adjusted", "weighted", "totalscore", "fcrate", "maxscorepercent"];

    private void PrecomputeRankingsPages(IReadOnlyList<string> instrumentKeys)
    {
        // Per-instrument page 1
        foreach (var instrument in instrumentKeys)
        {
            var db = _persistence.GetOrCreateInstrumentDb(instrument);
            foreach (var metric in RankingMetrics)
            {
                var (entries, total) = db.GetAccountRankings(metric, 1, 50);
                var entryList = entries.ToList();
                var names = _metaDb.GetDisplayNames(entryList.Select(e => e.AccountId));
                var enriched = entryList.Select(e => new
                {
                    e.AccountId,
                    displayName = names.GetValueOrDefault(e.AccountId),
                    e.SongsPlayed,
                    e.TotalChartedSongs,
                    e.Coverage,
                    e.RawSkillRating,
                    e.AdjustedSkillRating,
                    e.AdjustedSkillRank,
                    e.WeightedRating,
                    e.WeightedRank,
                    e.FcRate,
                    e.FcRateRank,
                    e.TotalScore,
                    e.TotalScoreRank,
                    e.MaxScorePercent,
                    e.MaxScorePercentRank,
                    e.AvgAccuracy,
                    e.FullComboCount,
                    e.AvgStars,
                    e.BestRank,
                    e.AvgRank,
                    e.ComputedAt,
                }).ToList();

                var payload = new
                {
                    instrument,
                    rankBy = metric,
                    page = 1,
                    pageSize = 50,
                    totalAccounts = total,
                    entries = enriched,
                };
                var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOpts);
                Store($"rankings:{instrument}:{metric}:1:50", jsonBytes);
            }
        }

        // Composite page 1
        foreach (var metric in RankingMetrics)
        {
            // Composite only supports "adjusted" as metric — skip others
            var (entries, total) = _metaDb.GetCompositeRankings(1, 50);
            var names = _metaDb.GetDisplayNames(entries.Select(e => e.AccountId));
            var enriched = entries.Select(e => new
            {
                e.AccountId,
                displayName = names.GetValueOrDefault(e.AccountId),
                e.InstrumentsPlayed,
                e.TotalSongsPlayed,
                e.CompositeRating,
                e.CompositeRank,
                instruments = new
                {
                    guitar = e.GuitarAdjustedSkill.HasValue ? new { skill = e.GuitarAdjustedSkill, rank = e.GuitarSkillRank } : null,
                    bass = e.BassAdjustedSkill.HasValue ? new { skill = e.BassAdjustedSkill, rank = e.BassSkillRank } : null,
                    drums = e.DrumsAdjustedSkill.HasValue ? new { skill = e.DrumsAdjustedSkill, rank = e.DrumsSkillRank } : null,
                    vocals = e.VocalsAdjustedSkill.HasValue ? new { skill = e.VocalsAdjustedSkill, rank = e.VocalsSkillRank } : null,
                    proGuitar = e.ProGuitarAdjustedSkill.HasValue ? new { skill = e.ProGuitarAdjustedSkill, rank = e.ProGuitarSkillRank } : null,
                    proBass = e.ProBassAdjustedSkill.HasValue ? new { skill = e.ProBassAdjustedSkill, rank = e.ProBassSkillRank } : null,
                },
                e.ComputedAt,
            }).ToList();

            var payload = new
            {
                page = 1,
                pageSize = 50,
                totalAccounts = total,
                entries = enriched,
            };
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOpts);
            Store($"rankings:composite:{metric}:1:50", jsonBytes);
            break; // Composite rankings are metric-agnostic — one page covers all
        }

        // Overview (top N per instrument for each metric)
        foreach (var metric in RankingMetrics)
        {
            var allAccountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var perInstrument = new Dictionary<string, (List<AccountRankingDto> Entries, int Total)>();
            foreach (var instrument in instrumentKeys)
            {
                var db = _persistence.GetOrCreateInstrumentDb(instrument);
                var (entries, total) = db.GetAccountRankings(metric, 1, 10);
                var entryList = entries.ToList();
                foreach (var e in entryList) allAccountIds.Add(e.AccountId);
                perInstrument[instrument] = (entryList, total);
            }

            var names = _metaDb.GetDisplayNames(allAccountIds);
            var result = new Dictionary<string, object>();
            foreach (var (instrument, (entries, total)) in perInstrument)
            {
                result[instrument] = new
                {
                    totalAccounts = total,
                    entries = entries.Select(e => new
                    {
                        e.AccountId,
                        displayName = names.GetValueOrDefault(e.AccountId),
                        e.AdjustedSkillRating,
                        e.AdjustedSkillRank,
                        e.WeightedRating,
                        e.WeightedRank,
                        e.FcRate,
                        e.FcRateRank,
                        e.TotalScore,
                        e.TotalScoreRank,
                        e.MaxScorePercent,
                        e.MaxScorePercentRank,
                        e.SongsPlayed,
                        e.Coverage,
                    }).ToList(),
                };
            }

            var overviewPayload = new
            {
                rankBy = metric,
                pageSize = 10,
                instruments = result,
            };
            var overviewBytes = JsonSerializer.SerializeToUtf8Bytes(overviewPayload, _jsonOpts);
            Store($"rankings:overview:{metric}:10", overviewBytes);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Phase 6: Neighborhoods (registered users × instruments)
    // ═══════════════════════════════════════════════════════════════

    private void PrecomputeNeighborhoods(
        HashSet<string> registeredIds,
        IReadOnlyList<string> instrumentKeys)
    {
        if (registeredIds.Count == 0) return;

        foreach (var accountId in registeredIds)
        {
            // Per-instrument neighborhoods
            foreach (var instrument in instrumentKeys)
            {
                try
                {
                    var db = _persistence.GetOrCreateInstrumentDb(instrument);
                    var (above, self, below) = db.GetAccountRankingNeighborhood(accountId, 5);
                    if (self is null) continue;

                    var allIds = above.Select(e => e.AccountId)
                        .Append(self.AccountId)
                        .Concat(below.Select(e => e.AccountId));
                    var names = _metaDb.GetDisplayNames(allIds);

                    object Map(AccountRankingDto e) => new
                    {
                        e.AccountId,
                        displayName = names.GetValueOrDefault(e.AccountId),
                        e.TotalScore,
                        e.TotalScoreRank,
                        e.SongsPlayed,
                        e.TotalChartedSongs,
                        e.Coverage,
                        e.AdjustedSkillRating,
                        e.AdjustedSkillRank,
                    };

                    var payload = new
                    {
                        instrument,
                        accountId,
                        rank = self.TotalScoreRank,
                        above = above.Select(Map).ToList(),
                        self = Map(self),
                        below = below.Select(Map).ToList(),
                    };
                    var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOpts);
                    Store($"neighborhood:{instrument}:{accountId}:5", jsonBytes);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to precompute neighborhood for {AccountId}/{Instrument}", accountId, instrument);
                }
            }

            // Composite neighborhood
            try
            {
                var (above, self, below) = _metaDb.GetCompositeRankingNeighborhood(accountId, 5);
                if (self is null) continue;

                var allIds = above.Select(e => e.AccountId)
                    .Append(self.AccountId)
                    .Concat(below.Select(e => e.AccountId));
                var names = _metaDb.GetDisplayNames(allIds);

                object Map(CompositeRankingDto e) => new
                {
                    e.AccountId,
                    displayName = names.GetValueOrDefault(e.AccountId),
                    e.CompositeRating,
                    e.CompositeRank,
                    e.InstrumentsPlayed,
                    e.TotalSongsPlayed,
                };

                var compositePayload = new
                {
                    accountId,
                    rank = self.CompositeRank,
                    above = above.Select(Map).ToList(),
                    self = Map(self),
                    below = below.Select(Map).ToList(),
                };
                var compositeBytes = JsonSerializer.SerializeToUtf8Bytes(compositePayload, _jsonOpts);
                Store($"neighborhood:composite:{accountId}:5", compositeBytes);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to precompute composite neighborhood for {AccountId}", accountId);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Phase 7: Static Data (firstseen)
    // ═══════════════════════════════════════════════════════════════

    private void PrecomputeFirstSeen()
    {
        var all = _metaDb.GetAllFirstSeenSeasons();
        var songs = all.Select(kvp => new
        {
            songId = kvp.Key,
            firstSeenSeason = kvp.Value.FirstSeenSeason,
            estimatedSeason = kvp.Value.EstimatedSeason,
        }).ToList();
        var payload = new { count = songs.Count, songs };
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOpts);
        Store("firstseen", jsonBytes);
    }

    // ═══════════════════════════════════════════════════════════════
    // Disk Persistence (for --precompute mode and warm startup)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Save all precomputed data to a directory as GZip'd JSON files.
    /// Called by the <c>--precompute</c> CLI mode.
    /// </summary>
    public async Task SaveToDiskAsync(string directory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);

        // Save the response store: { key → base64(gzip(json)) }
        var storeEntries = new Dictionary<string, string>(_store.Count);
        foreach (var (key, resp) in _store)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                await gz.WriteAsync(resp.Json, ct);
            storeEntries[key] = Convert.ToBase64String(ms.ToArray());
        }

        var storePath = Path.Combine(directory, "responses.json.gz");
        await using (var fs = File.Create(storePath))
        await using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
        {
            await JsonSerializer.SerializeAsync(gz, storeEntries, cancellationToken: ct);
        }

        // Save population tiers
        if (_populationTiers is not null)
        {
            var tiersPath = Path.Combine(directory, "population-tiers.json.gz");
            // Convert tuple keys to serializable format
            var serializable = new Dictionary<string, PopulationTierData>();
            foreach (var ((songId, instrument), data) in _populationTiers)
                serializable[$"{songId}|{instrument}"] = data;

            await using var fs = File.Create(tiersPath);
            await using var gzs = new GZipStream(fs, CompressionLevel.Optimal);
            await JsonSerializer.SerializeAsync(gzs, serializable, cancellationToken: ct);
        }

        _log.LogInformation("Saved {Count} precomputed responses to {Dir}", _store.Count, directory);
    }

    /// <summary>
    /// Load precomputed data from disk. Returns true if data was loaded.
    /// Called at service startup before the first scrape.
    /// </summary>
    public async Task<bool> LoadFromDiskAsync(string directory, CancellationToken ct = default)
    {
        var storePath = Path.Combine(directory, "responses.json.gz");
        if (!File.Exists(storePath))
            return false;

        try
        {
            // Load response store
            Dictionary<string, string>? storeEntries;
            await using (var fs = File.OpenRead(storePath))
            await using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            {
                storeEntries = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(gz, cancellationToken: ct);
            }

            if (storeEntries is not null)
            {
                foreach (var (key, b64) in storeEntries)
                {
                    var compressed = Convert.FromBase64String(b64);
                    using var ms = new MemoryStream(compressed);
                    using var gzd = new GZipStream(ms, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    await gzd.CopyToAsync(output, ct);
                    var json = output.ToArray();
                    var hash = SHA256.HashData(json);
                    var etag = $"\"{Convert.ToBase64String(hash, 0, 16)}\"";
                    _store[key] = new PrecomputedResponse(json, etag);
                }
            }

            // Load population tiers
            var tiersPath = Path.Combine(directory, "population-tiers.json.gz");
            if (File.Exists(tiersPath))
            {
                await using var fs = File.OpenRead(tiersPath);
                await using var gzd = new GZipStream(fs, CompressionMode.Decompress);
                var serializable = await JsonSerializer.DeserializeAsync<Dictionary<string, PopulationTierData>>(gzd, cancellationToken: ct);
                if (serializable is not null)
                {
                    var tiers = new Dictionary<(string, string), PopulationTierData>();
                    foreach (var (compoundKey, data) in serializable)
                    {
                        var parts = compoundKey.Split('|', 2);
                        if (parts.Length == 2)
                            tiers[(parts[0], parts[1])] = data;
                    }
                    _populationTiers = tiers;
                }
            }

            _log.LogInformation("Loaded {Count} precomputed responses from {Dir}", _store.Count, directory);
            return _store.Count > 0;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load precomputed data from {Dir}, will recompute", directory);
            return false;
        }
    }

    /// <summary>Default subdirectory name for precomputed data under the data directory.</summary>
    public const string PrecomputedSubdir = "precomputed";

    // ═══════════════════════════════════════════════════════════════
    // Add a wrapper on GlobalLeaderboardPersistence for stored rankings
    // ═══════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════
    // DTOs (internal, serialized to JSON)
    // ═══════════════════════════════════════════════════════════════

    internal sealed record PrecomputedResponse(byte[] Json, string ETag);

    internal sealed class PrecomputedPlayerScore
    {
        [JsonPropertyName("si")] public string SongId { get; init; } = "";
        [JsonPropertyName("ins")] public string Instrument { get; init; } = "";
        [JsonPropertyName("sc")] public int Score { get; init; }
        [JsonPropertyName("acc")] public int Accuracy { get; init; }
        [JsonPropertyName("fc")] public bool IsFullCombo { get; init; }
        [JsonPropertyName("st")] public int Stars { get; init; }
        [JsonPropertyName("dif")] public int Difficulty { get; init; }
        [JsonPropertyName("sn")] public int Season { get; init; }
        [JsonPropertyName("pct")] public double Percentile { get; init; }
        [JsonPropertyName("rk")] public int Rank { get; init; }
        [JsonPropertyName("et")] public string? EndTime { get; init; }
        [JsonPropertyName("te")] public int TotalEntries { get; init; }
        [JsonPropertyName("ml")] public double? MinLeeway { get; init; }
        [JsonPropertyName("vs")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<PrecomputedValidScore>? ValidScores { get; init; }
    }

    internal sealed class PrecomputedValidScore
    {
        [JsonPropertyName("sc")] public int Score { get; init; }
        [JsonPropertyName("acc")] public int? Accuracy { get; init; }
        [JsonPropertyName("fc")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IsFullCombo { get; init; }
        [JsonPropertyName("st")] public int? Stars { get; init; }
        [JsonPropertyName("ml")] public double MinLeeway { get; init; }
        [JsonPropertyName("rt")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<RankTier>? RankTiers { get; init; }
    }
}

/// <summary>Precomputed population data for a (songId, instrument) pair.</summary>
public sealed class PopulationTierData
{
    [JsonPropertyName("bc")] public int BaseCount { get; init; }
    [JsonPropertyName("t")] public List<PopulationTier> Tiers { get; init; } = new();
}

/// <summary>A single changepoint in the population tier curve. Leeway is a percentage (e.g. -5.0 = 5% below max).</summary>
public sealed record PopulationTier
{
    [JsonPropertyName("l")] public double Leeway { get; init; }
    [JsonPropertyName("t")] public int Total { get; init; }
}

/// <summary>A single changepoint in a fallback score's rank curve. Leeway is a percentage (e.g. -5.0 = 5% below max).</summary>
public sealed record RankTier
{
    [JsonPropertyName("l")] public double Leeway { get; init; }
    [JsonPropertyName("r")] public int Rank { get; init; }
}
