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
    private readonly IPathDataStore _pathDataStore;
    private readonly SharedDopPool _pool;
    private readonly ScrapeProgressTracker _progress;
    private readonly IOptions<ScraperOptions> _options;
    private readonly ILogger<ScrapeOrchestrator> _log;

    public ScrapeOrchestrator(
        GlobalLeaderboardScraper globalScraper,
        GlobalLeaderboardPersistence persistence,
        IPathDataStore IPathDataStore,
        SharedDopPool pool,
        ScrapeProgressTracker progress,
        IOptions<ScraperOptions> options,
        ILogger<ScrapeOrchestrator> log)
    {
        _globalScraper = globalScraper;
        _persistence = persistence;
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

        // Reset CDN cooldown state from any previous pass to avoid stale backoff
        _globalScraper.ResetCdnState();

        // Per-pass timeout as a safety net against infinite hangs
        using var passCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (opts.ScrapePassTimeoutMinutes > 0)
        {
            passCts.CancelAfter(TimeSpan.FromMinutes(opts.ScrapePassTimeoutMinutes));
            _log.LogDebug("Scrape pass timeout set to {Timeout} minutes.", opts.ScrapePassTimeoutMinutes);
        }
        var passCt = passCts.Token;

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

        // ── Staging-based persistence ──
        // Entries are staged to leaderboard_staging during scraping, then
        // finalized into the live leaderboard_entries table after all songs
        // complete. This replaces the previous pipelined channel writers.
        var aggregates = new GlobalLeaderboardPersistence.PipelineAggregates();
        _persistence.CleanupAbandonedStaging(scrapeId);
        int totalRequests = 0;
        long totalBytes = 0;

        _progress.SetSubOperation("fetching_leaderboards");

        var allResults = await _globalScraper.ScrapeManySongsAsync(
            scrapeRequests, accessToken, callerAccountId, opts.DegreeOfParallelism,
            onSongComplete: async (songId, results) =>
            {
                bool hasData = false;
                foreach (var result in results)
                {
                    Interlocked.Add(ref totalRequests, result.Requests);
                    Interlocked.Add(ref totalBytes, result.BytesReceived);

                    if (result.Entries.Count == 0)
                    {
                        // Record meta even for empty leaderboards
                        _persistence.Meta.UpsertStagingMeta(scrapeId, songId, result.Instrument,
                            new StagingMetaUpdate
                            {
                                ReportedPages = result.ReportedTotalPages,
                                PagesScraped = result.PagesScraped,
                                EntriesStaged = 0,
                                Requests = result.Requests,
                                BytesReceived = result.BytesReceived,
                                DeepScrapeStatus = result.DeferredDeepScrape is not null ? "eligible" : "none",
                            });
                        continue;
                    }
                    hasData = true;

                    // Deduplicate entries by account ID (same account can appear on
                    // multiple pages when Epic's leaderboard shifts between fetches).
                    // Keep the highest-score entry per account to match finalization semantics.
                    var deduped = result.Entries
                        .GroupBy(e => e.AccountId, StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.OrderByDescending(e => e.Score).First())
                        .ToList();

                    // Stage all entries (page number estimated from position)
                    var taggedEntries = deduped
                        .Select((e, i) => (PageNum: i / 100, Entry: e))
                        .ToList();
                    _persistence.Meta.StageChunk(scrapeId, songId, result.Instrument, taggedEntries);

                    // Record staging metadata
                    _persistence.Meta.UpsertStagingMeta(scrapeId, songId, result.Instrument,
                        new StagingMetaUpdate
                        {
                            ReportedPages = result.ReportedTotalPages,
                            PagesScraped = result.PagesScraped,
                            EntriesStaged = result.Entries.Count,
                            Requests = result.Requests,
                            BytesReceived = result.BytesReceived,
                            DeepScrapeStatus = result.DeferredDeepScrape is not null ? "eligible" : "none",
                        });

                    // Enqueue deep-scrape job if wave 2 is needed
                    if (result.DeferredDeepScrape is not null)
                    {
                        _persistence.Meta.EnqueueDeepScrapeJob(new DeepScrapeJobInfo
                        {
                            ScrapeId = scrapeId,
                            SongId = result.DeferredDeepScrape.SongId,
                            Instrument = result.DeferredDeepScrape.Instrument,
                            Label = result.DeferredDeepScrape.Label,
                            ValidCutoff = result.DeferredDeepScrape.ValidCutoff,
                            ValidEntryTarget = opts.ValidEntryTarget,
                            Wave2StartPage = result.DeferredDeepScrape.Wave2Start,
                            ReportedPages = result.DeferredDeepScrape.ReportedPages,
                            InitialValidCount = result.DeferredDeepScrape.InitialValidCount,
                        });
                    }

                    // Track registered user appearances for post-scrape refresh
                    if (registeredIds.Count > 0)
                    {
                        aggregates.AddSeenRegisteredEntries(
                            result.Entries
                                .Where(e => registeredIds.Contains(e.AccountId))
                                .Select(e => (e.AccountId, songId, result.Instrument)));
                    }

                    aggregates.AddEntries(result.Entries.Count);
                }
                if (hasData)
                {
                    aggregates.IncrementSongsWithData();
                    aggregates.AddChangedSongId(songId);
                }

                await ValueTask.CompletedTask;
            },
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
            validCutoffMultiplier: opts.ValidCutoffMultiplier);

        // ── Finalize all staged leaderboards into the live table ──
        _progress.SetSubOperation("finalizing_staged");
        var stagedMeta = _persistence.Meta.GetStagingMeta(scrapeId);
        int totalFinalized = 0;
        int totalScoreChanges = 0;
        foreach (var meta in stagedMeta)
        {
            if (meta.EntriesStaged == 0) continue;
            try
            {
                int wave = meta.DeepScrapeStatus == "eligible" ? 1 : 1;
                var (merged, changes) = _persistence.FinalizeLeaderboardFromStaging(
                    scrapeId, meta.SongId, meta.Instrument, registeredIds, wave);
                totalFinalized += merged;
                totalScoreChanges += changes;
                aggregates.AddChanges(changes);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Failed to finalize staged leaderboard {Song}/{Instrument}.",
                    meta.SongId, meta.Instrument);
            }
        }

        _log.LogInformation(
            "Finalized {Combos:N0} staged leaderboards: {Entries:N0} rows merged, {Changes:N0} score changes.",
            stagedMeta.Count(m => m.EntriesStaged > 0), totalFinalized, totalScoreChanges);

        // Checkpoint all WAL files after the heavy write phase to keep them small
        // and prevent auto-checkpoints from firing during API reads.
        _progress.SetSubOperation("checkpointing");
        _persistence.CheckpointAll();

        sw.Stop();

        // Save page estimate for next run
        var currentOp = _progress.GetProgressResponse().Current;
        SaveCachedPageEstimate(opts, currentOp?.Pages?.DiscoveredTotal ?? 0);

        // Complete scrape log
        _persistence.Meta.CompleteScrapeRun(scrapeId, aggregates.SongsWithData, aggregates.TotalEntries, totalRequests, totalBytes);

        _log.LogInformation(
            "Scrape run #{ScrapeId} complete. {Songs} songs with data, {Entries} entries, " +
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

        // Build the explicit output contract
        var ctx = new ScrapePassContext
        {
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
            SongsScraped = aggregates.SongsWithData,
            ScrapeDuration = sw.Elapsed,
        };
    }

    // ─── Scrape-specific utility methods ───────────────────────

    internal static IReadOnlyList<string> GetEnabledInstruments(ScraperOptions opts)
    {
        var instruments = new List<string>();
        if (opts.QueryLead)    instruments.Add("Solo_Guitar");
        if (opts.QueryBass)    instruments.Add("Solo_Bass");
        if (opts.QueryVocals)  instruments.Add("Solo_Vocals");
        if (opts.QueryDrums)   instruments.Add("Solo_Drums");
        if (opts.QueryProLead) instruments.Add("Solo_PeripheralGuitar");
        if (opts.QueryProBass) instruments.Add("Solo_PeripheralBass");
        return instruments;
    }

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
