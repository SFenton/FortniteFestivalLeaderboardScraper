using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FSTService;
using FSTService.Api;
using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// Precomputes JSON responses for registered players and popular leaderboard pages
/// during post-scrape, so API requests can be served from memory in &lt;1ms.
/// Entries are staged to a disk-backed channel during precomputation
/// and bulk-loaded to PostgreSQL at the end, keeping peak RAM bounded.
/// </summary>
public sealed class ScrapeTimePrecomputer
{
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly IMetaDatabase _metaDb;
    private readonly IPathDataStore _pathStore;
    private readonly ScrapeProgressTracker _progress;
    private readonly ILogger<ScrapeTimePrecomputer> _log;
    private readonly ILoggerFactory _loggerFactory;
    private readonly JsonSerializerOptions _jsonOpts;
    private readonly FeatureOptions _features;
    private readonly ScraperOptions _scraperOptions;
    private readonly LeaderboardRivalsCalculator? _leaderboardRivalsCalculator;
    private readonly SoloCurrentProjectionBuilder? _soloCurrentProjectionBuilder;
    private bool _currentProjectionAuthoritativeForPrecompute;

    /// <summary>
    /// Disk staging writer for bulk precomputation. Created per PrecomputeAllAsync call,
    /// null between scrapes. Single-user PrecomputeUser() bypasses this entirely.
    /// </summary>
    private DiskStagingWriter? _staging;

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
        ILoggerFactory loggerFactory,
        JsonSerializerOptions jsonOpts,
        FeatureOptions features,
        LeaderboardRivalsCalculator? leaderboardRivalsCalculator = null,
        SoloCurrentProjectionBuilder? soloCurrentProjectionBuilder = null)
        : this(persistence, metaDb, pathStore, progress, log, loggerFactory, jsonOpts, features, new ScraperOptions(), leaderboardRivalsCalculator, soloCurrentProjectionBuilder)
    {
    }

    public ScrapeTimePrecomputer(
        GlobalLeaderboardPersistence persistence,
        IMetaDatabase metaDb,
        IPathDataStore pathStore,
        ScrapeProgressTracker progress,
        ILogger<ScrapeTimePrecomputer> log,
        ILoggerFactory loggerFactory,
        JsonSerializerOptions jsonOpts,
        FeatureOptions features,
        ScraperOptions scraperOptions,
        LeaderboardRivalsCalculator? leaderboardRivalsCalculator = null,
        SoloCurrentProjectionBuilder? soloCurrentProjectionBuilder = null)
    {
        _persistence = persistence;
        _metaDb = metaDb;
        _pathStore = pathStore;
        _progress = progress;
        _log = log;
        _loggerFactory = loggerFactory;
        _jsonOpts = jsonOpts;
        _features = features;
        _scraperOptions = scraperOptions;
        _leaderboardRivalsCalculator = leaderboardRivalsCalculator;
        _soloCurrentProjectionBuilder = soloCurrentProjectionBuilder;
    }

    /// <summary>Returns a precomputed response if available, else null.</summary>
    public (byte[] Json, string ETag)? TryGet(string cacheKey)
    {
        return _metaDb.GetCachedResponse(cacheKey);
    }

    /// <summary>Gets the precomputed population tier data for the songs endpoint.</summary>
    public IReadOnlyDictionary<(string SongId, string Instrument), PopulationTierData>? GetPopulationTiers()
        => _populationTiers;

    /// <summary>
    /// Clears process-local precompute state. Called at scrape start.
    /// Published PostgreSQL responses must stay available as stale cache while public reads are frozen.
    /// </summary>
    public void InvalidateAll()
    {
        _populationTiers = null;
    }

    /// <summary>Number of records staged (during active precomputation) or 0.</summary>
    public long Count => _staging?.RecordCount ?? 0;

    /// <summary>
    /// Precompute all data: player profiles, leaderboard-all pages, and population tiers.
    /// Called after post-scrape enrichment is complete (ranks, backfill, rivals all done).
    ///
    /// Phases 2-7 are independent and run in parallel. All output is staged to a
    /// disk-backed channel, then bulk-loaded to PostgreSQL at the end.
    /// </summary>
    public Task PrecomputeAllAsync(CancellationToken ct)
        => PrecomputeAllAsync(_metaDb.ShouldShowLeaderboardEntryTotals(), ct);

    public async Task PrecomputeAllAsync(bool showLeaderboardEntryTotals, CancellationToken ct, bool publishImmediately = true)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _log.LogInformation(
            "Scrape-time precomputation starting. publishImmediately={PublishImmediately}, showLeaderboardEntryTotals={ShowLeaderboardEntryTotals}.",
            publishImmediately,
            showLeaderboardEntryTotals);
        var publicationPointers = _metaDb.GetPublicationPointerState();
        if (publishImmediately && publicationPointers.WorkingPublicationId.HasValue)
        {
            throw new InvalidOperationException(
                "Standalone precompute cannot publish while a working publication generation exists.");
        }

        var targetPublicationId = publishImmediately
            ? publicationPointers.CurrentPublicationId
            : publicationPointers.WorkingPublicationId
                ?? publicationPointers.CurrentPublicationId;
        var persistenceTargetPublicationId = targetPublicationId ?? 0;
        using var publicationBuildLease =
            _metaDb.AcquirePublicationCacheBuildLease(
                persistenceTargetPublicationId,
                requireCurrentPublication: publishImmediately);
        _metaDb.BulkSetCachedResponsesStaging(
            [],
            persistenceTargetPublicationId);
        var projectionStats = _soloCurrentProjectionBuilder?.Inspect();
        _currentProjectionAuthoritativeForPrecompute =
            projectionStats is
            {
                ProjectionExists: true,
                ScopeCount: > 0,
                FailedScopeCount: 0,
            };
        var allMaxScores = _pathStore.GetAllMaxScores();
        var unfilteredPopulation = _metaDb.GetAllLeaderboardPopulation();
        var registeredIds = _metaDb.GetRegisteredAccountIds();
        var instrumentKeys = _persistence.GetInstrumentKeys();

        // ── Set up disk staging (shared across phases 2-7) ──────
        await using var staging = new DiskStagingWriter(
            _loggerFactory.CreateLogger<DiskStagingWriter>(),
            Path.Combine(_scraperOptions.DataDirectory, "precompute-staging"));
        _staging = staging;

        // ── Phase 1: Leeway metadata (must complete before player phases) ──
        _progress.SetSubOperation("population_tiers");
        var leewayMetadata = ComputeLeewayMetadata(allMaxScores, instrumentKeys);
        var tiers = leewayMetadata.PopulationTiers;
        _populationTiers = tiers;
        StoreLeaderboardRankOffsets(leewayMetadata.RankOffsets);
        _log.LogInformation("Precomputed population tiers for {Count} (song, instrument) pairs and rank offsets for {OffsetCount} pairs in {Elapsed}ms.",
            tiers.Count, leewayMetadata.RankOffsets.Count, sw.ElapsedMilliseconds);

        _log.LogInformation("Building scrape-time band scores cache for player precomputation.");
        var bandScoresCache = BuildBandScoresCache(allMaxScores, instrumentKeys);
        _log.LogInformation("Built scrape-time band scores cache for {Count:N0} (song, instrument) pair(s).", bandScoresCache.Count);

        // ── Phases 2-7: Independent. Run sequentially by default so API latency
        // remains the priority while post-scrape work is active.
        _progress.SetSubOperation("parallel_precompute");
        if (_scraperOptions.RunPrecomputePhasesInParallel)
        {
            var phase2 = Task.Run(() =>
            {
                PrecomputePlayersAsync(registeredIds, allMaxScores, unfilteredPopulation,
                    tiers, bandScoresCache, ct).GetAwaiter().GetResult();
            }, ct);
            var phase3 = Task.Run(() =>
            {
                PrecomputeLeaderboardAll(allMaxScores, unfilteredPopulation, instrumentKeys, showLeaderboardEntryTotals, leewayMetadata.RankOffsetsByKey);
                PrecomputeSongBandLeaderboardsAll(showLeaderboardEntryTotals);
            }, ct);
            var phase4 = Task.Run(() =>
            {
                PrecomputePlayerSubResourcesAsync(registeredIds, instrumentKeys, ct)
                    .GetAwaiter().GetResult();
            }, ct);
            var phase5 = Task.Run(() => PrecomputeRankingsPages(instrumentKeys), ct);
            var phase6 = Task.Run(() => PrecomputeNeighborhoods(registeredIds, instrumentKeys), ct);
            var phase7 = Task.Run(() => PrecomputeFirstSeen(), ct);

            await Task.WhenAll(phase2, phase3, phase4, phase5, phase6, phase7);
        }
        else
        {
            await PrecomputePlayersAsync(registeredIds, allMaxScores, unfilteredPopulation,
                tiers, bandScoresCache, ct);
            PrecomputeLeaderboardAll(allMaxScores, unfilteredPopulation, instrumentKeys, showLeaderboardEntryTotals, leewayMetadata.RankOffsetsByKey);
            PrecomputeSongBandLeaderboardsAll(showLeaderboardEntryTotals);
            await PrecomputePlayerSubResourcesAsync(registeredIds, instrumentKeys, ct);
            PrecomputeRankingsPages(instrumentKeys);
            PrecomputeNeighborhoods(registeredIds, instrumentKeys);
            PrecomputeFirstSeen();
        }

        // ── Signal channel completion and wait for drain to disk ──
        _log.LogInformation("Scrape-time precomputation phases complete; draining {Count:N0} staged records to disk.", staging.RecordCount);
        staging.Complete();
        await staging.WaitForDrainAsync();

        // ── Flush from staging file to PostgreSQL staging table, then atomic swap ──
        _log.LogInformation("All phases complete. {Count:N0} records staged to disk.", staging.RecordCount);
        staging.FlushToPostgres(
            _metaDb,
            useStaging: true,
            publicationId: persistenceTargetPublicationId);
        _log.LogInformation("Scrape-time precompute cache staging flush complete. publishImmediately={PublishImmediately}.", publishImmediately);
        if (publishImmediately)
        {
            _log.LogInformation("Swapping scrape-time precompute cache staging responses into the live cache.");
            _metaDb.SwapCachedResponsesFromStaging(
                persistenceTargetPublicationId);
            _log.LogInformation("Scrape-time precompute cache staging responses published.");
        }
        _staging = null;

        sw.Stop();
        _log.LogInformation(
            publishImmediately
                ? "Scrape-time precomputation complete: {PlayerCount} players in {Elapsed}s."
                : "Scrape-time precomputation staged for publication: {PlayerCount} players in {Elapsed}s.",
            registeredIds.Count, sw.Elapsed.TotalSeconds);
    }

    private static string ExtractAccountId(string cacheKey)
    {
        // "player:{accountId}:::" → extract accountId
        if (!cacheKey.StartsWith("player:", StringComparison.Ordinal)) return string.Empty;
        var end = cacheKey.IndexOf(':', 7);
        return end < 0 ? cacheKey[7..] : cacheKey[7..end];
    }

    internal static string PlayerHistoryCacheKey(string accountId)
        => $"history:v2:{accountId}";

    /// <summary>
    /// Precompute a single player (e.g., after /track registration between scrapes).
    /// Covers profile + all sub-resources (stats, history, sync-status, rivals, lb-rivals).
    /// Does not mutate published or shared staging cache state. A complete
    /// publication rebuild owns cache promotion.
    /// </summary>
    public void PrecomputeUser(string accountId)
    {
        _log.LogInformation(
            "Deferred single-user precompute for {AccountId}; " +
            "a complete publication rebuild owns cache generation.",
            accountId);
    }

    // ═══════════════════════════════════════════════════════════════
    // Population Tiers
    // ═══════════════════════════════════════════════════════════════

    private Dictionary<(string, string), PopulationTierData> ComputePopulationTiers(
        Dictionary<string, SongMaxScores> allMaxScores,
        IReadOnlyList<string> instrumentKeys)
        => ComputeLeewayMetadata(allMaxScores, instrumentKeys).PopulationTiers;

    private LeewayMetadata ComputeLeewayMetadata(
        Dictionary<string, SongMaxScores> allMaxScores,
        IReadOnlyList<string> instrumentKeys)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new ConcurrentDictionary<(string, string), PopulationTierData>();
        var rankOffsets = new ConcurrentBag<LeaderboardRankOffsetData>();

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

        _log.LogInformation(
            "Computing leeway metadata for {Count:N0} (song, instrument) pair(s) with maxDegree={MaxDegree}.",
            workItems.Count,
            8);
        var completed = 0;
        var lastLogged = 0;
        Parallel.ForEach(workItems, new ParallelOptions { MaxDegreeOfParallelism = 8 }, item =>
        {
            var (songId, instrument, maxScore) = item;
            var db = _persistence.GetOrCreateInstrumentDb(instrument);
            var lowerBound = (int)(maxScore * 0.95);
            var upperBound = (int)(maxScore * 1.05);

            var baseCount = db.GetCurrentStatePopulationAtOrBelow(songId, lowerBound);
            var bandScores = db.GetCurrentStateScoresInBand(songId, lowerBound, upperBound);

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

            rankOffsets.Add(LeaderboardRankOffsetCalculator.Compute(
                songId,
                instrument,
                maxScore,
                db.GetCurrentStateRankOffsetCoverage(songId),
                baseCount,
                bandScores));

            var current = Interlocked.Increment(ref completed);
            if (ShouldLogPrecomputeProgress(current, workItems.Count, sw.Elapsed, ref lastLogged))
            {
                _log.LogInformation(
                    "Leeway metadata progress: {Completed:N0}/{Total:N0} pairs ({Percent:P1}) in {Elapsed:n1}s.",
                    current,
                    workItems.Count,
                    current / (double)Math.Max(1, workItems.Count),
                    sw.Elapsed.TotalSeconds);
            }
        });

        var populationTiers = new Dictionary<(string, string), PopulationTierData>(result);
        var offsets = rankOffsets.ToList();
        var offsetsByKey = offsets.ToDictionary(offset => (offset.SongId, offset.Instrument));
        _log.LogInformation(
            "Leeway metadata complete: {Completed:N0}/{Total:N0} pairs, {TierCount:N0} tier entries, {OffsetCount:N0} rank offset entries in {Elapsed:n1}s.",
            completed,
            workItems.Count,
            populationTiers.Count,
            offsets.Count,
            sw.Elapsed.TotalSeconds);
        return new LeewayMetadata(populationTiers, offsets, offsetsByKey);
    }

    private void StoreLeaderboardRankOffsets(IReadOnlyList<LeaderboardRankOffsetData> offsets)
    {
        foreach (var offset in offsets)
        {
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(offset, _jsonOpts);
            Store(LeaderboardCacheKeys.LeaderboardRankOffsets(offset.SongId, offset.Instrument), jsonBytes);
        }
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
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cache = new ConcurrentDictionary<(string, string), int[]>();
        var workItems = new List<(string SongId, string Instrument, int MaxScore)>();
        foreach (var (songId, ms) in allMaxScores)
            foreach (var inst in instrumentKeys)
            {
                var max = ms.GetByInstrument(inst);
                if (max.HasValue && max.Value > 0) workItems.Add((songId, inst, max.Value));
            }

        _log.LogInformation(
            "Building band scores cache for {Count:N0} (song, instrument) pair(s) with maxDegree={MaxDegree}.",
            workItems.Count,
            8);
        var completed = 0;
        var lastLogged = 0;
        Parallel.ForEach(workItems, new ParallelOptions { MaxDegreeOfParallelism = 8 }, item =>
        {
            var db = _persistence.GetOrCreateInstrumentDb(item.Instrument);
            var lo = (int)(item.MaxScore * 0.95);
            var hi = (int)(item.MaxScore * 1.05);
            var scores = db.GetCurrentStateScoresInBand(item.SongId, lo, hi);
            cache[(item.SongId, item.Instrument)] = scores.ToArray();

            var current = Interlocked.Increment(ref completed);
            if (ShouldLogPrecomputeProgress(current, workItems.Count, sw.Elapsed, ref lastLogged))
            {
                _log.LogInformation(
                    "Band scores cache progress: {Completed:N0}/{Total:N0} pairs ({Percent:P1}) in {Elapsed:n1}s.",
                    current,
                    workItems.Count,
                    current / (double)Math.Max(1, workItems.Count),
                    sw.Elapsed.TotalSeconds);
            }
        });

        _log.LogInformation(
            "Band scores cache complete: {Completed:N0}/{Total:N0} pairs, {CachedScoreCount:N0} score(s) cached in {Elapsed:n1}s.",
            completed,
            workItems.Count,
            cache.Values.Sum(static scores => scores.Length),
            sw.Elapsed.TotalSeconds);
        return new Dictionary<(string, string), int[]>(cache);
    }

    private static bool ShouldLogPrecomputeProgress(int completed, int total, TimeSpan elapsed, ref int lastLogged)
    {
        if (completed >= total)
            return true;

        var logEvery = Math.Max(100, total / 20);
        if (completed - lastLogged < logEvery)
            return false;

        var previous = Interlocked.Exchange(ref lastLogged, completed);
        return completed > previous;
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
        var failures = new ConcurrentBag<Exception>();

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
                    failures.Add(new InvalidOperationException(
                        $"Player profile precompute failed for {accountId}.",
                        ex));
                }
                return ValueTask.CompletedTask;
            });
        ThrowIfPrecomputeFailures("player profiles", failures);
    }

    internal void PrecomputeSinglePlayer(
        string accountId,
        Dictionary<string, SongMaxScores> allMaxScores,
        Dictionary<(string SongId, string Instrument), long> unfilteredPopulation,
        IReadOnlyDictionary<(string, string), PopulationTierData> populationTiers,
        Dictionary<(string, string), int[]> bandScoresCache,
        Dictionary<string, string>? displayNames = null,
        List<(string Key, byte[] Json, string ETag)>? storeOverride = null)
    {
        var scores = _persistence.GetCurrentStatePlayerProfile(accountId);
        if (scores.Count == 0 && !_currentProjectionAuthoritativeForPrecompute)
            scores = _persistence.GetPlayerProfile(accountId);

        displayNames ??= _metaDb.GetDisplayNames(new[] { accountId });
        var displayName = displayNames.GetValueOrDefault(accountId);

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

        // Get most recent play date per (songId, instrument) from score_history
        var lastPlayedDates = _metaDb.GetLastPlayedDates(accountId);

        var enriched = new List<PrecomputedPlayerScore>(scores.Count);
        var fallbackVariantCount = 0;
        var fallbackRankTierCount = 0;
        var fallbackStoredRankCount = 0;
        var fallbackMissingRankCount = 0;
        foreach (var s in scores)
        {
            var key = (s.SongId, s.Instrument);
            var rank = s.ApiRank > 0 ? s.ApiRank : s.Rank;
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
                        populationTiers.TryGetValue(key, out var populationTierData);
                        var rankTiers = ComputeRankTiers(fb.Score, maxVal.Value, bandScores, fb.Rank, populationTierData);
                        fallbackVariantCount++;
                        fallbackRankTierCount += rankTiers?.Count ?? 0;
                        if (fb.Rank.HasValue) fallbackStoredRankCount++;
                        else fallbackMissingRankCount++;

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
                LastPlayedAt = lastPlayedDates.GetValueOrDefault(key),
                MinLeeway = minLeeway,
                ValidScores = validScores,
            });
        }

        if (fallbackVariantCount > 0)
        {
            _log.LogInformation(
                "[Precompute.PlayerProfileFallbacks] account={AccountId} current_scores={CurrentScores} fallback_variants={FallbackVariants} rank_tiers={RankTiers} stored_rank_variants={StoredRankVariants} missing_rank_variants={MissingRankVariants}",
                accountId,
                scores.Count,
                fallbackVariantCount,
                fallbackRankTierCount,
                fallbackStoredRankCount,
                fallbackMissingRankCount);
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
        Store(cacheKey, jsonBytes, storeOverride);
    }

    /// <summary>
    /// Compute rank tiers (changepoints) for a specific fallback score.
    /// Uses pre-fetched band scores and stored historical ranks to avoid
    /// per-fallback current-state DB queries during scrape-time precompute.
    /// </summary>
    internal static List<RankTier>? ComputeRankTiers(
        int fallbackScore,
        int maxScore,
        int[]? bandScores,
        int? storedRank,
        PopulationTierData? populationTierData = null)
    {
        if (maxScore <= 0)
            return storedRank.HasValue ? [new RankTier { Leeway = -5.0, Rank = storedRank.Value }] : null;

        var lowerBound = (int)(maxScore * 0.95);
        var sortedBandScores = bandScores ?? [];

        if (sortedBandScores.Length == 0)
        {
            if (storedRank.HasValue)
                return [new RankTier { Leeway = -5.0, Rank = storedRank.Value }];

            if (populationTierData is not null && fallbackScore <= lowerBound)
                return [new RankTier { Leeway = -5.0, Rank = Math.Max(1, populationTierData.BaseCount) }];

            return null;
        }

        // The band scores are sorted ascending. Scores below the band are always
        // valid at -5%. For fallbacks in that always-valid zone, use the stored
        // historical rank as the initial rank. For fallbacks inside the band,
        // no below-band score can outrank it, so the initial rank starts at 1.
        var estimatedAlwaysValidRank = populationTierData is null ? 1 : populationTierData.BaseCount + 1;
        var alwaysAbove = fallbackScore <= lowerBound
            ? Math.Max(0, (storedRank ?? estimatedAlwaysValidRank) - 1)
            : 0;

        var tiers = new List<RankTier>();
        int cumAboveFallback = alwaysAbove;
        double prevLeeway = double.NegativeInfinity;
        int prevRank = -1;

        // At leeway = -5.0 (i.e. -5.0%, everything below 0.95*max), rank is alwaysAbove + 1
        int baseRankForTier = alwaysAbove + 1;
        tiers.Add(new RankTier { Leeway = -5.0, Rank = baseRankForTier });
        prevRank = baseRankForTier;
        prevLeeway = -5.0;

        foreach (var score in sortedBandScores)
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
        IReadOnlyList<string> instrumentKeys,
        bool showLeaderboardEntryTotals,
        IReadOnlyDictionary<(string SongId, string Instrument), LeaderboardRankOffsetData> rankOffsets)
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

        var songParallelism = Math.Max(1, _scraperOptions.PrecomputeLeaderboardSongParallelism);
        var failures = new ConcurrentBag<Exception>();
        Parallel.ForEach(allSongIds, new ParallelOptions { MaxDegreeOfParallelism = songParallelism }, songId =>
        {
            try
            {
                // No-leeway variant
                PrecomputeLeaderboardAllForSong(songId, null, allMaxScores, unfilteredPopulation, instrumentKeys, showLeaderboardEntryTotals, rankOffsets);
                // Leeway=1 variant
                PrecomputeLeaderboardAllForSong(songId, 1.0, allMaxScores, unfilteredPopulation, instrumentKeys, showLeaderboardEntryTotals, rankOffsets);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to precompute leaderboard-all for song {SongId}", songId);
                failures.Add(new InvalidOperationException(
                    $"Leaderboard-all precompute failed for {songId}.",
                    ex));
            }
        });
        ThrowIfPrecomputeFailures("leaderboard-all", failures);
    }

    private void PrecomputeLeaderboardAllForSong(
        string songId, double? leeway,
        Dictionary<string, SongMaxScores> allMaxScores,
        Dictionary<(string SongId, string Instrument), long> unfilteredPopulation,
        IReadOnlyList<string> instrumentKeys,
        bool showLeaderboardEntryTotals,
        IReadOnlyDictionary<(string SongId, string Instrument), LeaderboardRankOffsetData> rankOffsets)
    {
        var instrumentArr = instrumentKeys.ToArray();
        var rawResults = new (string Instrument, List<LeaderboardEntryDto> Entries, int DbCount, int TotalEntries, bool UseFilteredRank, int? ExactRemovedAbove)?[instrumentArr.Length];

        var instrumentParallelism = Math.Max(1, _scraperOptions.PrecomputeLeaderboardInstrumentParallelism);
        Parallel.For(0, instrumentArr.Length, new ParallelOptions { MaxDegreeOfParallelism = instrumentParallelism }, i =>
        {
            var instrument = instrumentArr[i];
            int? maxScore = null;
            if (leeway.HasValue && allMaxScores.TryGetValue(songId, out var ms))
            {
                var raw = ms.GetByInstrument(instrument);
                if (raw.HasValue) maxScore = (int)(raw.Value * (1.0 + leeway.Value / 100.0));
            }
            var useFilteredRank = maxScore.HasValue;
            int? exactRemovedAbove = null;
            if (leeway.HasValue
                && rankOffsets.TryGetValue((songId, instrument), out var offsetData)
                && LeaderboardRankOffsetCalculator.TryGetExactRemovedAbove(offsetData, leeway.Value, out var removedAbove))
            {
                exactRemovedAbove = removedAbove;
            }
            var result = _persistence.GetCurrentStateLeaderboardWithCount(songId, instrument, 10, maxScore: maxScore);
            if (result is null) return;

            var (entries, dbCount) = result.Value;
            var popKey = (songId, instrument);
            var totalEntries = Math.Max(
                unfilteredPopulation.TryGetValue(popKey, out var pop) && pop > 0 ? (int)pop : 0,
                dbCount);

            rawResults[i] = (instrument, entries, dbCount, totalEntries, useFilteredRank, exactRemovedAbove);
        });

        var allAccountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rawInstruments = new List<(string Instrument, List<LeaderboardEntryDto> Entries, int DbCount, int TotalEntries, bool UseFilteredRank, int? ExactRemovedAbove)>();
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
                Rank = ri.UseFilteredRank
                    ? LeaderboardResponseRanks.Resolve(e.ApiRank, e.Rank, e.Rank, true, ri.ExactRemovedAbove)
                    : e.Rank,
                LocalRank = ri.UseFilteredRank ? e.Rank : (int?)null,
                ApiRank = e.ApiRank > 0 ? e.ApiRank : (int?)null,
                RankSource = ri.UseFilteredRank
                    ? LeaderboardResponseRanks.ResolveSource(e.ApiRank, e.Rank, e.Rank, true, ri.ExactRemovedAbove)
                    : LeaderboardResponseRanks.ComputedRankSource,
                e.Accuracy,
                e.IsFullCombo,
                e.Stars,
                e.Difficulty,
                e.Season,
                e.Percentile,
                e.EndTime,
            }).ToList(),
        }).ToList();

        var payload = new { songId, showLeaderboardEntryTotals, instruments };
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOpts);
        var cacheKey = LeaderboardCacheKeys.LeaderboardAll(songId, LeaderboardCacheKeys.SongDetailPreviewTop, leeway);
        Store(cacheKey, jsonBytes);
    }

    private void PrecomputeSongBandLeaderboardsAll(bool showLeaderboardEntryTotals)
    {
        var songIds = _metaDb.GetBandLeaderboardSongIds();
        if (songIds.Count == 0) return;

        var failures = new ConcurrentBag<Exception>();
        Parallel.ForEach(songIds, new ParallelOptions { MaxDegreeOfParallelism = 4 }, songId =>
        {
            try
            {
                PrecomputeSongBandLeaderboardsAllForSong(songId, showLeaderboardEntryTotals);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to precompute song band leaderboards for song {SongId}", songId);
                failures.Add(new InvalidOperationException(
                    $"Song band leaderboard precompute failed for {songId}.",
                    ex));
            }
        });
        ThrowIfPrecomputeFailures("song band leaderboards", failures);
    }

    private void PrecomputeSongBandLeaderboardsAllForSong(string songId, bool showLeaderboardEntryTotals)
    {
        var bands = BandInstrumentMapping.AllBandTypes.Select(bandType =>
        {
            var (entries, totalEntries) = _metaDb.GetSongBandLeaderboard(
                songId,
                bandType,
                LeaderboardCacheKeys.SongDetailPreviewTop,
                0);
            var names = _metaDb.GetDisplayNames(entries.SelectMany(entry => entry.Members.Select(member => member.AccountId)));
            return new
            {
                bandType,
                count = entries.Count,
                totalEntries,
                localEntries = totalEntries,
                entries = MapSongBandLeaderboardEntries(entries, names),
                selectedPlayerEntry = (object?)null,
                selectedBandEntry = (object?)null,
            };
        }).ToList();

        var payload = new { songId, showLeaderboardEntryTotals, bands };
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOpts);
        var cacheKey = LeaderboardCacheKeys.SongBandLeaderboardsAll(songId, LeaderboardCacheKeys.SongDetailPreviewTop);
        Store(cacheKey, jsonBytes);
    }

    private static List<object> MapSongBandLeaderboardEntries(
        IEnumerable<SongBandLeaderboardEntryDto> entries,
        IReadOnlyDictionary<string, string> names) =>
        entries.Select(entry => MapSongBandLeaderboardEntry(entry, names)).ToList();

    private static object MapSongBandLeaderboardEntry(
        SongBandLeaderboardEntryDto entry,
        IReadOnlyDictionary<string, string> names) => new
        {
            entry.BandId,
            entry.BandType,
            entry.TeamKey,
            entry.ComboId,
            Members = entry.Members.Select(member => new
            {
                member.AccountId,
                DisplayName = names.GetValueOrDefault(member.AccountId),
                member.Instruments,
                member.Score,
                member.Accuracy,
                member.IsFullCombo,
                member.Stars,
                member.Difficulty,
                member.Season,
            }).ToList(),
            entry.Score,
            entry.Rank,
            entry.Accuracy,
            entry.IsFullCombo,
            entry.Stars,
            entry.Difficulty,
            entry.Season,
            entry.Percentile,
            entry.EndTime,
        };

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Store a precomputed cache entry. During PrecomputeAllAsync this writes to
    /// the disk staging channel. The optional storeOverride collects entries in a
    /// list for the single-user PrecomputeUser() path. As a fallback (e.g. when
    /// called outside of bulk precomputation), writes directly to PostgreSQL.
    /// </summary>
    private void Store(
        string cacheKey,
        byte[] json,
        List<(string Key, byte[] Json, string ETag)>? storeOverride = null)
        => StoreEntries(
            cacheKey,
            json,
            storeOverride,
            publicRequestTargets: null);

    private void StoreWithPublicRequestTargets(
        string cacheKey,
        byte[] json,
        IReadOnlyList<string> publicRequestTargets)
        => StoreEntries(
            cacheKey,
            json,
            storeOverride: null,
            publicRequestTargets);

    private void StorePublicRequestTargets(
        byte[] json,
        IReadOnlyList<string> publicRequestTargets)
    {
        foreach (var requestTarget in publicRequestTargets)
        {
            Store(
                PublicApiResponseCachePolicy
                    .BuildCacheKeyForRequestTarget(requestTarget),
                json);
        }
    }

    private void StoreEntries(
        string cacheKey,
        byte[] json,
        List<(string Key, byte[] Json, string ETag)>? storeOverride,
        IReadOnlyList<string>? publicRequestTargets)
    {
        var hash = SHA256.HashData(json);
        var etag = $"\"{Convert.ToBase64String(hash, 0, 16)}\"";

        StoreEntry(cacheKey);
        if (publicRequestTargets is not null)
        {
            foreach (var requestTarget in publicRequestTargets)
            {
                StoreEntry(
                    PublicApiResponseCachePolicy
                        .BuildCacheKeyForRequestTarget(requestTarget));
            }
        }

        void StoreEntry(string key)
        {
            if (storeOverride is not null)
            {
                storeOverride.Add((key, json, etag));
                return;
            }

            if (_staging is not null)
            {
                _staging.Write(key, json, etag);
                return;
            }

            _log.LogDebug(
                "Skipped direct precomputed cache write for {CacheKey}; " +
                "a complete publication rebuild owns cache promotion.",
                key);
        }
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
        var failures = new ConcurrentBag<Exception>();

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
                    PrecomputePlayerLeaderboardRivals(
                        accountId,
                        instrumentKeys,
                        displayNames,
                        allowLiveFallback: _scraperOptions.PrecomputeLiveLeaderboardRivals);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to precompute sub-resources for {AccountId}", accountId);
                    failures.Add(new InvalidOperationException(
                        $"Player sub-resource precompute failed for {accountId}.",
                        ex));
                }
                return ValueTask.CompletedTask;
            });
        ThrowIfPrecomputeFailures("player sub-resources", failures);
    }

    private void PrecomputePlayerStats(string accountId,
        List<(string Key, byte[] Json, string ETag)>? storeOverride = null)
    {
        var tierRows = _metaDb.GetPlayerStatsTiers(accountId);
        if (tierRows.Count == 0) return;

        int totalSongs = _persistence.GetTotalSongCount();

        // Embed composite ranks so the stats endpoint serves them without a second DB hit
        var composite = _metaDb.GetCompositeRanking(accountId);
        object? compositeRanks = composite is null ? null : new
        {
            adjusted = composite.CompositeRank,
            weighted = composite.CompositeRankWeighted,
            fcRate = composite.CompositeRankFcRate,
            totalScore = composite.CompositeRankTotalScore,
            maxScore = composite.CompositeRankMaxScore,
        };

        var familyRanks = BuildSoloFamilyRankPayload(_metaDb.GetSoloFamilyRankingsForAccount(accountId));

        // Expose canonical per-instrument ranks; alternate leeway tiers are retired.
        var instrumentRanks = BuildInstrumentRankPayload(accountId);
        var bands = _persistence.GetPlayerBands(accountId);

        var payload = new
        {
            accountId,
            totalSongs,
            compositeRanks,
            familyRanks,
            // Keep the compatibility property even when null; null-valued properties are otherwise omitted.
            instrumentRanks = (object?)instrumentRanks ?? JsonSerializer.Deserialize<JsonElement>("null"),
            bands,
            instruments = tierRows.Select(r => new
            {
                ins = r.Instrument == "Overall" ? "00" : ComboIds.FromInstruments(new[] { r.Instrument }),
                tiers = JsonSerializer.Deserialize<JsonElement>(r.TiersJson),
            }).ToList(),
        };
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOpts);
        Store($"playerstats:{accountId}", jsonBytes, storeOverride);
    }

    private static object? BuildSoloFamilyRankPayload(IReadOnlyDictionary<string, SoloFamilyRankingDto> rankings)
    {
        if (rankings.Count == 0)
            return null;

        return rankings.ToDictionary(
            kvp => kvp.Key,
            kvp => (object)new
            {
                scopeId = kvp.Value.ScopeId,
                adjusted = kvp.Value.AdjustedSkillRank,
                weighted = kvp.Value.WeightedRank,
                fcRate = kvp.Value.FcRateRank,
                totalScore = kvp.Value.TotalScoreRank,
                maxScore = kvp.Value.MaxScorePercentRank,
                songsPlayed = kvp.Value.SongsPlayed,
                totalChartedSongs = kvp.Value.TotalChartedSongs,
                coverage = kvp.Value.Coverage,
                fullComboCount = kvp.Value.FullComboCount,
                totalRankedAccounts = kvp.Value.TotalRankedAccounts,
            },
            StringComparer.OrdinalIgnoreCase);
    }

    private static object MapSoloFamilyRanking(SoloFamilyRankingDto ranking, IReadOnlyDictionary<string, string> names) => new
    {
        ranking.AccountId,
        displayName = names.GetValueOrDefault(ranking.AccountId),
        ranking.SongsPlayed,
        ranking.TotalChartedSongs,
        ranking.Coverage,
        ranking.RawSkillRating,
        ranking.AdjustedSkillRating,
        ranking.AdjustedSkillRank,
        ranking.WeightedRating,
        ranking.WeightedRank,
        ranking.FcRate,
        ranking.FcRateRank,
        ranking.TotalScore,
        ranking.TotalScoreRank,
        ranking.MaxScorePercent,
        ranking.MaxScorePercentRank,
        avgAccuracy = 0,
        ranking.FullComboCount,
        avgStars = 0,
        bestRank = 0,
        avgRank = 0,
        ranking.RawMaxScorePercent,
        ranking.RawWeightedRating,
        ranking.ComputedAt,
        ranking.TotalRankedAccounts,
    };

    /// <summary>
    /// Build canonical per-instrument rank data.
    /// Returns an array of { ins, base: {ranks}, tiers: [] }.
    /// </summary>
    private List<object>? BuildInstrumentRankPayload(string accountId)
    {
        var instrumentKeys = _persistence.GetInstrumentKeys();
        var result = new List<object>();

        foreach (var instrument in instrumentKeys)
        {
            var db = _persistence.GetOrCreateInstrumentDb(instrument);
            var baseRanking = db.GetAccountRanking(accountId);
            if (baseRanking is null) continue;

            var baseAdj = baseRanking.AdjustedSkillRank;
            var baseWgt = baseRanking.WeightedRank;
            var baseFc = baseRanking.FcRateRank;
            var baseTs = baseRanking.TotalScoreRank;
            var baseMs = baseRanking.MaxScorePercentRank;

            result.Add(new
            {
                ins = ComboIds.FromInstruments(new[] { instrument }),
                totalRanked = db.GetRankedAccountCount(),
                @base = new { adjusted = baseAdj, weighted = baseWgt, fcRate = baseFc, totalScore = baseTs, maxScore = baseMs },
                tiers = Array.Empty<object>(),
            });
        }

        return result.Count > 0 ? result : null;
    }

    private void PrecomputePlayerHistory(string accountId,
        List<(string Key, byte[] Json, string ETag)>? storeOverride = null)
    {
        var history = _metaDb.GetScoreHistory(accountId, int.MaxValue);
        var payload = new
        {
            accountId,
            count = history.Count,
            history,
        };
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOpts);
        Store(PlayerHistoryCacheKey(accountId), jsonBytes, storeOverride);
    }

    private void PrecomputePlayerSyncStatus(string accountId,
        List<(string Key, byte[] Json, string ETag)>? storeOverride = null)
    {
        var backfill = _metaDb.GetBackfillStatus(accountId);
        var historyRecon = _metaDb.GetHistoryReconStatus(accountId);
        var rivals = _metaDb.GetRivalsStatus(accountId);
        var backfillDisplay = backfill is null
            ? null
            : _metaDb.GetBackfillSongProgress(accountId, backfill.SongsChecked, backfill.TotalSongsToCheck);

        var payload = new
        {
            accountId,
            isTracked = _metaDb.IsAccountRegistered(accountId),
            pendingRankUpdate = backfill?.RankingsPending ?? false,
            backfill = backfill is null ? null : new
            {
                status = backfill.Status,
                songsChecked = backfill.SongsChecked,
                totalSongsToCheck = backfill.TotalSongsToCheck,
                displaySongsChecked = backfillDisplay?.SongsChecked,
                displayTotalSongs = backfillDisplay?.TotalSongs,
                entriesFound = backfill.EntriesFound,
                startedAt = backfill.StartedAt,
                completedAt = backfill.CompletedAt,
                rankingsPending = backfill.RankingsPending,
                deferredReason = backfill.DeferredReason,
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
        Store($"syncstatus:{accountId}", jsonBytes, storeOverride);
    }

    private void PrecomputePlayerRivalsOverview(string accountId,
        List<(string Key, byte[] Json, string ETag)>? storeOverride = null)
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
        Store($"rivals-overview:{accountId}", jsonBytes, storeOverride);
    }

    private void PrecomputePlayerRivalsAll(string accountId, Dictionary<string, string> displayNames,
        List<(string Key, byte[] Json, string ETag)>? storeOverride = null)
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
        Store($"rivals-all:{accountId}", jsonBytes, storeOverride);
    }

    internal void PrecomputePlayerLeaderboardRivals(
        string accountId,
        IReadOnlyList<string> instrumentKeys,
        Dictionary<string, string> displayNames,
        bool allowLiveFallback,
        List<(string Key, byte[] Json, string ETag)>? storeOverride = null)
    {
        var persistedInstruments = 0;
        var liveFallbackInstruments = 0;
        var skippedInstruments = 0;

        foreach (var instrument in instrumentKeys)
        {
            LeaderboardInstrumentRivalsResult? live = null;
            var rivals = _metaDb.GetLeaderboardRivals(accountId, instrument, "totalscore");
            if (rivals.Count > 0)
            {
                persistedInstruments++;
            }
            else if (allowLiveFallback && _leaderboardRivalsCalculator is not null)
            {
                live = _leaderboardRivalsCalculator.ComputeInstrument(accountId, instrument, "totalscore");
                rivals = live.Rivals.ToList();
                liveFallbackInstruments++;
            }
            else
            {
                skippedInstruments++;
                continue;
            }

            if (rivals.Count == 0 && live is null)
                continue;

            var rivalNames = _metaDb.GetDisplayNames(rivals.Select(r => r.RivalAccountId));
            foreach (var kv in displayNames)
                rivalNames.TryAdd(kv.Key, kv.Value);

            int? userRank;
            if (live is not null)
            {
                userRank = live.GetUserRank("totalscore");
                if (!live.UserFound && live.Rivals.Count == 0)
                    continue;
            }
            else
            {
                userRank = rivals.Count == 0 ? null : rivals[0].UserRank;
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
            Store($"lb-rivals:{accountId}:{instrument}:totalscore", jsonBytes, storeOverride);
        }

        if (persistedInstruments > 0 || liveFallbackInstruments > 0 || skippedInstruments > 0)
        {
            _log.LogInformation(
                "[Precompute.LeaderboardRivals] account={AccountId} persisted_instruments={PersistedInstruments} live_fallback_instruments={LiveFallbackInstruments} skipped_instruments={SkippedInstruments}",
                accountId,
                persistedInstruments,
                liveFallbackInstruments,
                skippedInstruments);
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

    private sealed record LeewayMetadata(
        Dictionary<(string SongId, string Instrument), PopulationTierData> PopulationTiers,
        IReadOnlyList<LeaderboardRankOffsetData> RankOffsets,
        IReadOnlyDictionary<(string SongId, string Instrument), LeaderboardRankOffsetData> RankOffsetsByKey);

    // ═══════════════════════════════════════════════════════════════
    // Phase 5: Rankings Pages (page 1 for each instrument × metric)
    // ═══════════════════════════════════════════════════════════════

    private static readonly string[] RankingMetrics = ["adjusted", "weighted", "totalscore", "fcrate", "maxscore"];

    private void PrecomputeRankingsPages(IReadOnlyList<string> instrumentKeys)
    {
        // Canonical per-instrument page 1.
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
                    e.RawMaxScorePercent,
                    e.RawWeightedRating,
                    e.ComputedAt,
                }).ToList();

                var payload = new
                {
                    instrument,
                    rankBy = metric,
                    page = 1,
                    pageSize = 50,
                    totalAccounts = total,
                    leeway = (double?)null,
                    entries = enriched,
                };
                var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOpts);
                var escapedInstrument =
                    Uri.EscapeDataString(instrument);
                var escapedMetric = Uri.EscapeDataString(metric);
                var page50Targets = new List<string>
                {
                    $"/api/rankings/{escapedInstrument}?rankBy={escapedMetric}&page=1&pageSize=50",
                    $"/api/rankings/{escapedInstrument}?rankBy={escapedMetric}",
                };
                if (metric == "adjusted")
                {
                    page50Targets.AddRange(
                    [
                        $"/api/rankings/{escapedInstrument}?page=1&pageSize=50",
                        $"/api/rankings/{escapedInstrument}",
                    ]);
                }

                StoreWithPublicRequestTargets(
                    $"rankings:{instrument}:{metric}:1:50",
                    jsonBytes,
                    page50Targets);

                foreach (var pageSize in new[] { 10, 25 })
                {
                    var projected =
                        CacheHelper.ProjectFirstPageSubset(
                            jsonBytes,
                            requestedPage: 1,
                            requestedPageSize: pageSize)
                        ?? throw new InvalidOperationException(
                            $"Could not project {instrument}/{metric} rankings to page size {pageSize}.");
                    var projectedTargets = new List<string>
                    {
                        $"/api/rankings/{escapedInstrument}?rankBy={escapedMetric}&page=1&pageSize={pageSize}",
                    };
                    if (metric == "adjusted")
                    {
                        projectedTargets.Add(
                            $"/api/rankings/{escapedInstrument}?page=1&pageSize={pageSize}");
                    }

                    StorePublicRequestTargets(
                        projected,
                        projectedTargets);
                }
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

        // Fixed solo family page 1 for each metric
        foreach (var scope in SoloFamilyRankingScopes.All)
        {
            foreach (var metric in RankingMetrics)
            {
                var (entries, total) = _metaDb.GetSoloFamilyRankings(scope.ScopeId, metric, 1, 50);
                var names = _metaDb.GetDisplayNames(entries.Select(e => e.AccountId));
                var payload = new
                {
                    scopeId = scope.ScopeId,
                    rankBy = metric,
                    page = 1,
                    pageSize = 50,
                    totalAccounts = total,
                    entries = entries.Select(e => MapSoloFamilyRanking(e, names)).ToList(),
                };
                var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOpts);
                Store($"rankings:family:{scope.ScopeId}:{metric}:1:50", jsonBytes);
            }
        }

        // Generic band ranking page 1 for each band type and metric. Selected
        // player/team variants can degrade to this snapshot while public reads
        // are frozen.
        foreach (var bandType in BandInstrumentMapping.AllBandTypes)
        {
            foreach (var metric in RankingMetrics)
            {
                var (entries, totalTeams) = _metaDb.GetBandTeamRankings(bandType, comboId: null, metric, page: 1, pageSize: 50);
                var entryList = entries.ToList();
                var names = _metaDb.GetDisplayNames(entryList.SelectMany(entry => entry.TeamMembers));
                var payload = new
                {
                    bandType,
                    comboId = (string?)null,
                    rankBy = metric,
                    page = 1,
                    pageSize = 50,
                    totalTeams,
                    entries = entryList.Select(entry => MapBandRanking(entry, names)).ToList(),
                    selectedPlayerEntry = (object?)null,
                    selectedBandEntry = (object?)null,
                };
                var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOpts);
                Store($"rankings:bands:{bandType}:{metric}:1:50", jsonBytes);
            }
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

    private static object MapBandRanking(BandTeamRankingDto ranking, IReadOnlyDictionary<string, string> names) => new
    {
        ranking.BandId,
        comboId = ranking.ComboId,
        ranking.TeamKey,
        teamMembers = ranking.TeamMembers.Select(accountId => new
        {
            accountId,
            displayName = names.GetValueOrDefault(accountId),
        }).ToList(),
        members = ranking.Members.Select(member => new
        {
            member.AccountId,
            displayName = names.GetValueOrDefault(member.AccountId),
            member.Instruments,
        }).ToList(),
        configurations = ranking.Configurations.Select(configuration => new
        {
            configuration.RawInstrumentCombo,
            configuration.ComboId,
            configuration.Instruments,
            configuration.AssignmentKey,
            configuration.AppearanceCount,
            configuration.MemberInstruments,
        }).ToList(),
        ranking.SongsPlayed,
        ranking.TotalChartedSongs,
        ranking.Coverage,
        ranking.RawSkillRating,
        ranking.AdjustedSkillRating,
        ranking.AdjustedSkillRank,
        ranking.WeightedRating,
        ranking.WeightedRank,
        ranking.FcRate,
        ranking.FcRateRank,
        ranking.TotalScore,
        ranking.TotalScoreRank,
        ranking.AvgAccuracy,
        ranking.FullComboCount,
        ranking.AvgStars,
        ranking.BestRank,
        ranking.AvgRank,
        ranking.RawWeightedRating,
        ranking.ComputedAt,
    };

    // ═══════════════════════════════════════════════════════════════
    // Phase 6: Neighborhoods (registered users × instruments)
    // ═══════════════════════════════════════════════════════════════

    private void PrecomputeNeighborhoods(
        HashSet<string> registeredIds,
        IReadOnlyList<string> instrumentKeys)
    {
        if (registeredIds.Count == 0) return;

        var failures = new ConcurrentBag<Exception>();
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
                    failures.Add(new InvalidOperationException(
                        $"Ranking neighborhood precompute failed for {accountId}/{instrument}.",
                        ex));
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
                failures.Add(new InvalidOperationException(
                    $"Composite neighborhood precompute failed for {accountId}.",
                    ex));
            }
        }
        ThrowIfPrecomputeFailures("ranking neighborhoods", failures);
    }

    private static void ThrowIfPrecomputeFailures(
        string phase,
        ConcurrentBag<Exception> failures)
    {
        if (failures.IsEmpty)
            return;

        throw new AggregateException(
            $"Scrape-time precompute failed for {phase} ({failures.Count} failure(s)).",
            failures);
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
            calculationVersion = kvp.Value.CalculationVersion,
        }).ToList();
        var payload = new { count = songs.Count, songs };
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOpts);
        Store("firstseen", jsonBytes);
    }

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
        [JsonPropertyName("lp")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LastPlayedAt { get; init; }
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
