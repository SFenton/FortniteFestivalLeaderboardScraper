using System.Diagnostics;
using FortniteFestival.Core;
using FortniteFestival.Core.Services;
using FSTService.Persistence;
using Microsoft.Extensions.Options;

namespace FSTService.Scraping;

/// <summary>
/// Orchestrates the core global leaderboard scrape pass (phases 2–8).
/// Owns scrape-specific concerns: building requests, pipelined scraping,
/// population updates, and progress tracking. Returns a <see cref="ScrapePassResult"/>
/// as an explicit output contract for downstream orchestrators.
/// </summary>
public sealed class ScrapeOrchestrator
{
    private readonly GlobalLeaderboardScraper _globalScraper;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly BandLeaderboardPersistence _bandPersistence;
    private readonly IPathDataStore _pathDataStore;
    private readonly SharedDopPool _pool;
    private readonly ScrapeProgressTracker _progress;
    private readonly IOptions<ScraperOptions> _options;
    private readonly ILogger<ScrapeOrchestrator> _log;

    public ScrapeOrchestrator(
        GlobalLeaderboardScraper globalScraper,
        GlobalLeaderboardPersistence persistence,
        BandLeaderboardPersistence bandPersistence,
        IPathDataStore IPathDataStore,
        SharedDopPool pool,
        ScrapeProgressTracker progress,
        IOptions<ScraperOptions> options,
        ILogger<ScrapeOrchestrator> log)
    {
        _globalScraper = globalScraper;
        _persistence = persistence;
        _bandPersistence = bandPersistence;
        _pathDataStore = IPathDataStore;
        _pool = pool;
        _progress = progress;
        _options = options;
        _log = log;
    }

    /// <summary>
    /// Run a full global leaderboard scrape pass: build requests, scrape all
    /// songs via V1 alltime, persist via pipelined writers, update population.
    /// </summary>
    public async Task<ScrapePassResult> RunAsync(
        string accessToken,
        string callerAccountId,
        FestivalService service,
        CancellationToken ct)
    {
        var opts = _options.Value;
        var resolvedPhases = opts.ResolvedPhases;
        bool doSoloScrape = resolvedPhases.HasFlag(ScrapePhase.SoloScrape);
        bool doBandScrape = resolvedPhases.HasFlag(ScrapePhase.BandScrape);

        // Reset CDN cooldown state from any previous pass to avoid stale backoff
        _globalScraper.ResetCdnState();

        // Reset DOP to initial configured value so a CDN slash from a previous
        // pass doesn't leave us stuck at minDop for the next pass.
        _pool.ResetDop();

        var passCt = ct;

        // Start scrape log entry
        var scrapeId = _persistence.Meta.StartScrapeRun();
        _log.LogInformation("Scrape run #{ScrapeId} started.", scrapeId);

        // Load registered account IDs for change detection
        var registeredIds = _persistence.Meta.GetRegisteredAccountIds();
        if (registeredIds.Count > 0)
            _log.LogInformation("{Count} registered user(s) will be tracked for score changes.", registeredIds.Count);

        // Build scrape requests: one per song, all enabled instruments.
        var enabledInstruments = GetEnabledInstruments(opts);
        var allMaxScores = _pathDataStore.GetAllMaxScores();
        var scrapeRequests = service.Songs
            .Where(s => s.track?.su is not null)
            .Select(song => new GlobalLeaderboardScraper.SongScrapeRequest
            {
                SongId = song.track.su,
                Instruments = enabledInstruments,
                Label = song.track.tt,
                MaxScores = allMaxScores.TryGetValue(song.track.su, out var ms) ? ms : null,
            })
            .ToList();

        _log.LogInformation("Scraping {SongCount} songs across {InstrumentCount} instrument types (DOP={Dop})...",
            scrapeRequests.Count, enabledInstruments.Count, opts.DegreeOfParallelism);

        var sw = Stopwatch.StartNew();

        // ── Initialize progress tracker ──
        int totalLeaderboards = scrapeRequests.Sum(r => r.Instruments.Count);
        int cachedPages = LoadCachedPageEstimate(opts);
        _progress.BeginPass(totalLeaderboards, scrapeRequests.Count, cachedPages);

        var instrumentTotals = enabledInstruments
            .ToDictionary(i => i, _ => scrapeRequests.Count);
        _progress.SetInstrumentTotals(instrumentTotals);

        // ── Disk-spool persistence (post-fetch flush) ──
        // Fetched pages are appended to per-instrument files on real disk.
        // No consumers run during fetch — zero PG write load, flat memory.
        // After fetch completes: drop indexes → bulk flush → recreate indexes.
        var spoolDir = Path.Combine(Path.GetFullPath(opts.DataDirectory), "spool");
        var aggregates = new GlobalLeaderboardPersistence.PipelineAggregates();
        int totalRequests = 0;
        long totalBytes = 0;

        _persistence.StartSpoolWriter(scrapeId, spoolDir);

        // Band spool — separate files for band_entries tables
        SpoolWriter<BandLeaderboardEntry>? bandSpool = null;
        bool hasBandTypes = doBandScrape;
        if (hasBandTypes)
        {
            bandSpool = BandSpoolWriterFactory.Create(_log, _bandPersistence, spoolDir);
        }

        // Snapshot registered users' current scores for change detection at end.
        var previousState = registeredIds.Count > 0
            ? _persistence.SnapshotRegisteredUsers(registeredIds)
            : new();

        _progress.SetSubOperation("fetching_leaderboards");

        // Split instruments into solo and band groups so they run as independent
        // ScrapeManySongsAsync calls sharing the same DOP pool.  Band 500 retries
        // no longer stall solo song completion.
        var soloInstruments = enabledInstruments.Where(i => !IsBandInstrument(i)).ToList();
        var bandInstruments = doBandScrape
            ? BandInstrumentMapping.AllBandTypes.ToList()
            : new List<string>();

        // Build per-group scrape requests (same songs, different instrument lists)
        var soloRequests = scrapeRequests.Select(r => new GlobalLeaderboardScraper.SongScrapeRequest
        {
            SongId = r.SongId, Instruments = soloInstruments, Label = r.Label, MaxScores = r.MaxScores,
        }).ToList();

        // Shared callback for solo results
        ValueTask OnSoloSongComplete(string songId, List<GlobalLeaderboardResult> results)
        {
            bool hasData = false;
            foreach (var result in results)
            {
                Interlocked.Add(ref totalRequests, result.Requests);
                Interlocked.Add(ref totalBytes, result.BytesReceived);

                if (result.EntriesCount == 0) continue;
                hasData = true;

                if (result.Entries.Count > 0)
                {
                    _persistence.EnqueueSpoolPage(songId, result.Instrument, result.Entries);
                    aggregates.AddRankChangedSongId(songId);

                    if (registeredIds.Count > 0)
                    {
                        aggregates.AddSeenRegisteredEntries(
                            result.Entries
                                .Where(e => registeredIds.Contains(e.AccountId))
                                .Select(e => (e.AccountId, songId, result.Instrument)));
                    }
                }

                aggregates.AddEntries(result.EntriesCount);
            }
            if (hasData)
            {
                aggregates.IncrementSongsWithData();
                aggregates.AddChangedSongId(songId);
            }
            return ValueTask.CompletedTask;
        }

        // Band scrape: flat parallel page fetcher using SharedDopPool for
        // low-priority DOP gating.  Band requests share the same AIMD limiter
        // as solo but are capped to LowPriorityPercent when solo is active.
        Task? bandTask = null;
        BandPageFetcher? bandFetcher = null;
        if (bandInstruments.Count > 0 && bandSpool is not null)
        {
            var bandSongIds = scrapeRequests.Select(r => r.SongId).ToList();
            bandFetcher = new BandPageFetcher(
                _globalScraper.Executor, _pool, bandSpool, _progress, _log);
            bandTask = bandFetcher.FetchAllAsync(
                bandSongIds, bandInstruments, accessToken, callerAccountId,
                opts.MaxPagesPerLeaderboard, passCt);
        }

        // Solo scrape task — only if SoloScrape phase is enabled
        Dictionary<string, List<GlobalLeaderboardResult>> allResults;
        if (doSoloScrape)
        {
            // Register solo as a high-priority phase so the SharedDopPool enforces
            // the low-priority gate on band for the duration of solo fetching.
            // Band is capped to LowPriorityPercent of DOP while solo is active;
            // when solo finishes, band naturally gravitates to 100%.
            _pool.BeginHighPriorityPhase();
            var soloTask = _globalScraper.ScrapeManySongsAsync(
                soloRequests, accessToken, callerAccountId, opts.DegreeOfParallelism,
                onSongComplete: OnSoloSongComplete,
                passCt,
                maxPages: opts.MaxPagesPerLeaderboard,
                sequential: opts.SequentialScrape,
                pageConcurrency: opts.PageConcurrency,
                songConcurrency: opts.SongConcurrency,
                maxRequestsPerSecond: opts.MaxRequestsPerSecond,
                overThresholdMultiplier: opts.OverThresholdMultiplier,
                overThresholdExtraPages: opts.OverThresholdExtraPages,
                validEntryTarget: opts.ValidEntryTarget,
                sharedLimiter: _pool.Limiter,
                deferDeepScrape: true,
                validCutoffMultiplier: opts.ValidCutoffMultiplier,
                onBandPageScraped: null);

        // Wait for solo only — band runs independently in the background.
        // Solo post-processing (flush, score changes, rankings) proceeds immediately.
            allResults = await soloTask;
            _pool.EndHighPriorityPhase();

            // ── Post-fetch bulk flush for solo: drop solo indexes → flush → recreate ──
            _progress.SetSubOperation("dropping_solo_indexes");
            _persistence.DropSoloIndexes();

            _progress.SetSubOperation("flushing_solo");
            await _persistence.FlushSpoolAsync(_progress);

            _progress.SetSubOperation("creating_solo_indexes");
            _persistence.CreateSoloIndexes();

            // ── Detect score changes for registered users (solo data only) ──
            _progress.SetSubOperation("detecting_score_changes");
            int totalScoreChanges = 0;
            if (registeredIds.Count > 0)
            {
                var changes = _persistence.DetectScoreChanges(previousState, registeredIds);
                if (changes.Count > 0)
                {
                    _persistence.Meta.InsertScoreChanges(changes);
                    totalScoreChanges = changes.Count;
                    aggregates.AddChanges(totalScoreChanges);
                }
                _log.LogInformation("{Changes:N0} score changes detected for registered users.", totalScoreChanges);
            }

            // Checkpoint all WAL files after the heavy write phase to keep them small
            // and prevent auto-checkpoints from firing during API reads.
            _progress.SetSubOperation("checkpointing");
            _persistence.CheckpointAll();
        }
        else
        {
            _log.LogInformation("Solo scrape skipped (not in enabled phases).");
            allResults = new();
        }

        sw.Stop();

        // Save page estimate for next run
        var currentOp = _progress.GetProgressResponse().Current;
        SaveCachedPageEstimate(opts, currentOp?.Pages?.DiscoveredTotal ?? 0);

        _log.LogInformation(
            "Scrape run #{ScrapeId} core checkpoint reached. {Songs} songs with data, {Entries} entries, " +
            "{Requests} HTTP requests, {Bytes} bytes, {Changes} score changes detected, elapsed={Elapsed:F1}s",
            scrapeId, aggregates.SongsWithData, aggregates.TotalEntries, totalRequests, totalBytes,
            aggregates.TotalChanges, sw.Elapsed.TotalSeconds);

        // Report entry counts per instrument
        var counts = _persistence.GetEntryCounts();
        foreach (var (instrument, count) in counts)
            _log.LogInformation("  {Instrument}: {Count:N0} entries", instrument, count);

        // ── Update leaderboard population from Epic's reported totalPages ──
        _progress.SetSubOperation("updating_population");
        var populationItems = new List<(string SongId, string Instrument, long TotalEntries)>();
        foreach (var (_, results) in allResults)
            foreach (var r in results)
                if (r.ReportedTotalPages > 0)
                {
                    long totalEntries = r.ReportedTotalPages <= 100
                        ? r.EntriesCount
                        : (long)r.ReportedTotalPages * 100;
                    populationItems.Add((r.SongId, r.Instrument, totalEntries));
                }
        if (populationItems.Count > 0)
        {
            _persistence.Meta.UpsertLeaderboardPopulation(populationItems);
            _log.LogInformation("Updated leaderboard population for {Count:N0} song/instrument pairs from Epic page counts.",
                populationItems.Count);
        }

        // ── Band: await completion and flush (runs in background during solo post-processing) ──
        if (bandTask is not null && bandSpool is not null)
        {
            _progress.SetSubOperation("awaiting_band");
            await bandTask;

            Interlocked.Add(ref totalRequests, (int)Interlocked.Read(ref bandFetcher!.TotalRequests));
            Interlocked.Add(ref totalBytes, Interlocked.Read(ref bandFetcher.TotalBytes));

            _progress.SetSubOperation("dropping_band_indexes");
            _persistence.DropBandIndexes();

            _progress.SetSubOperation("flushing_band");
            bandSpool.Complete();
            _log.LogInformation("Flushing band spool: {Records:N0} pages, {Entries:N0} entries...",
                bandSpool.RecordCount, bandSpool.EntryCount);
            await Task.Run(() => bandSpool.FlushAll(
                maxBatchPages: 64,
                onProgress: ReportBandSpoolFlushProgress));
            await bandSpool.DisposeAsync();

            _progress.SetSubOperation("creating_band_indexes");
            _persistence.CreateBandIndexes();

            _log.LogInformation("Band flush complete.");
        }

        // Build the explicit output contract
        var ctx = new ScrapePassContext
        {
            ScrapeId = scrapeId,
            AccessToken = accessToken,
            CallerAccountId = callerAccountId,
            RegisteredIds = registeredIds,
            Aggregates = aggregates,
            ScrapeRequests = scrapeRequests,
            DegreeOfParallelism = opts.DegreeOfParallelism,
        };

        return new ScrapePassResult
        {
            Context = ctx,
            ScrapeId = scrapeId,
            TotalRequests = totalRequests,
            TotalBytes = totalBytes,
            TotalEntries = aggregates.TotalEntries,
            SongsScraped = aggregates.SongsWithData,
            ScrapeDuration = sw.Elapsed,
        };
    }

    // ─── Scrape-specific utility methods ───────────────────────

    internal static IReadOnlyList<string> GetEnabledInstruments(ScraperOptions opts)
    {
        var instruments = new List<string>();
        if (opts.QueryLead)       instruments.Add("Solo_Guitar");
        if (opts.QueryBass)       instruments.Add("Solo_Bass");
        if (opts.QueryVocals)     instruments.Add("Solo_Vocals");
        if (opts.QueryDrums)      instruments.Add("Solo_Drums");
        if (opts.QueryProLead)    instruments.Add("Solo_PeripheralGuitar");
        if (opts.QueryProBass)    instruments.Add("Solo_PeripheralBass");
        if (opts.QueryProVocals)  instruments.Add("Solo_PeripheralVocals");
        if (opts.QueryProCymbals) instruments.Add("Solo_PeripheralCymbals");
        if (opts.QueryProDrums)   instruments.Add("Solo_PeripheralDrums");
        // Band types are scraped via BandPageFetcher (flat parallel) — not
        // included here to avoid double-scraping through ScrapeManySongsAsync.
        return instruments;
    }

    private void ReportBandSpoolFlushProgress(SpoolWriter<BandLeaderboardEntry>.FlushProgress flushProgress)
    {
        _progress.ReportFlushProgress(
            flushProgress.Label,
            flushProgress.Instrument,
            flushProgress.InstrumentsCompleted,
            flushProgress.InstrumentsTotal,
            flushProgress.PagesFlushed,
            flushProgress.PagesTotal,
            flushProgress.EntriesFlushed,
            flushProgress.EntriesTotal,
            flushProgress.InstrumentPagesFlushed,
            flushProgress.InstrumentPagesTotal,
            flushProgress.InstrumentEntriesFlushed,
            flushProgress.InstrumentEntriesTotal,
            flushProgress.ChunkIndex,
            flushProgress.ChunkTotal,
            flushProgress.ChunkPages,
            flushProgress.ChunkEntries,
            flushProgress.State,
            flushProgress.ActiveChunkElapsedSeconds,
            flushProgress.UpdatedAtUtc);
    }

    /// <summary>Returns true if the instrument key is a band type (Duets/Trios/Quad).</summary>
    internal static bool IsBandInstrument(string instrument) =>
        instrument.StartsWith("Band_", StringComparison.Ordinal);

    internal static int LoadCachedPageEstimate(ScraperOptions opts)
    {
        try
        {
            var path = Path.Combine(Path.GetFullPath(opts.DataDirectory), "page-estimate.json");
            if (!File.Exists(path)) return 0;
            var json = File.ReadAllText(path);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("totalPages", out var tp))
                return tp.GetInt32();
        }
        catch { }
        return 0;
    }

    internal static void SaveCachedPageEstimate(ScraperOptions opts, int totalPages)
    {
        try
        {
            var path = Path.Combine(Path.GetFullPath(opts.DataDirectory), "page-estimate.json");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
            {
                totalPages,
                savedAt = DateTime.UtcNow.ToString("o"),
            }));
        }
        catch { }
    }
}
