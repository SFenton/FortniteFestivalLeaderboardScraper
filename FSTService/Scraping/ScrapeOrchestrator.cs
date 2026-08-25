using System.Collections.Concurrent;
using System.Diagnostics;
using FortniteFestival.Core;
using FortniteFestival.Core.Persistence;
using FortniteFestival.Core.Services;
using FSTService.Auth;
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
    private readonly WorkerStatusPublisher? _workerStatus;
    private readonly object _activeBandSpoolLock = new();
    private SpoolWriter<BandLeaderboardEntry>? _activeBandSpool;

    public ScrapeOrchestrator(
        GlobalLeaderboardScraper globalScraper,
        GlobalLeaderboardPersistence persistence,
        BandLeaderboardPersistence bandPersistence,
        IPathDataStore IPathDataStore,
        SharedDopPool pool,
        ScrapeProgressTracker progress,
        IOptions<ScraperOptions> options,
        ILogger<ScrapeOrchestrator> log,
        WorkerStatusPublisher? workerStatus = null)
    {
        _globalScraper = globalScraper;
        _persistence = persistence;
        _bandPersistence = bandPersistence;
        _pathDataStore = IPathDataStore;
        _pool = pool;
        _progress = progress;
        _options = options;
        _log = log;
        _workerStatus = workerStatus;
    }

    /// <summary>
    /// Run a full global leaderboard scrape pass: build requests, scrape all
    /// songs via V1 alltime, persist via pipelined writers, update population.
    /// </summary>
    public async Task<ScrapePassResult> RunAsync(
        string accessToken,
        string callerAccountId,
        FestivalService service,
        SongCatalogPersistenceToken catalogToken,
        CancellationToken ct,
        TokenManager? tokenManager = null)
    {
        var opts = _options.Value;
        var resolvedPhases = opts.ResolvedPhases;
        bool doSoloScrape = resolvedPhases.HasFlag(ScrapePhase.SoloScrape);
        bool doBandScrape = resolvedPhases.HasFlag(ScrapePhase.BandScrape);
        var accessTokenProvider = tokenManager is not null
            ? new ScrapeAccessTokenProvider(tokenManager, accessToken, _log)
            : null;

        // Reset CDN cooldown state from any previous pass to avoid stale backoff
        _globalScraper.ResetCdnState();

        // Reset DOP to initial configured value so a CDN slash from a previous
        // pass doesn't leave us stuck at minDop for the next pass.
        _pool.ResetDop();

        var passCt = ct;
        var catalogSongs = service.Songs
            .Where(static song => song.track?.su is not null)
            .ToArray();
        var catalogSnapshot = SongCatalogSnapshotBuilder.Create(catalogSongs);
        SongCatalogSnapshotBuilder.ValidateToken(
            catalogSnapshot,
            catalogToken);

        // Start scrape log entry
        var scrapeId = _persistence.Meta.StartScrapeRun(catalogToken);
        var publicationId = _persistence.Meta
            .GetPublicationGenerationForScrape(scrapeId)?
            .PublicationId
            ?? throw new InvalidOperationException(
                $"Scrape {scrapeId} has no working publication generation.");
        using var pathPublicationScope =
            _pathDataStore.BeginPublicationRead(publicationId);
        _globalScraper.RefreshSongInstrumentSupport();
        _workerStatus?.AttachScrape(scrapeId);
        _log.LogInformation("Scrape run #{ScrapeId} started.", scrapeId);
        _persistence.CleanupAbandonedStaging(scrapeId);

        // Load registered account IDs for change detection
        var registeredIds = _persistence.Meta.GetRegisteredAccountIds();
        if (registeredIds.Count > 0)
            _log.LogInformation("{Count} registered user(s) will be tracked for score changes.", registeredIds.Count);

        // Build scrape requests: one per song, all enabled instruments.
        var enabledInstruments = GetEnabledInstruments(opts);
        var allMaxScores = _pathDataStore.GetAllMaxScores();
        var scrapeRequests = catalogSongs
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
        var persistedSongsWithData = new ConcurrentDictionary<string, byte>(
            StringComparer.OrdinalIgnoreCase);
        var writerResults = new List<WriterDrainResult>();
        LeaderboardScopeCoverageResult? soloCoverageResult = null;
        Exception? soloCoverageFailure = null;
        int totalRequests = 0;
        long totalBytes = 0;

        var useOnlineSoloWriter = opts.LeaderboardWriteMode == LeaderboardWriteMode.OnlineBounded;
        if (useOnlineSoloWriter && _persistence.WriteLegacyLiveLeaderboardDuringScrape)
        {
            _log.LogWarning("Scraper LeaderboardWriteMode=OnlineBounded is only enabled for snapshot-only scrape writes. Falling back to DiskSpool because legacy live writes are enabled.");
            useOnlineSoloWriter = false;
        }

        if (doSoloScrape)
        {
            if (useOnlineSoloWriter)
            {
                _persistence.StartOnlineSoloWriter(
                    scrapeId,
                    opts.BoundedChannelCapacity,
                    opts.OnlineWriteBatchPages,
                    opts.OnlineDbWriterConcurrency,
                    spoolDir,
                    passCt);
            }
            else
            {
                _persistence.StartSpoolWriter(scrapeId, spoolDir);
            }
        }

        // Band spool — separate files for band_entries tables
        SpoolWriter<BandLeaderboardEntry>? bandSpool = null;
        bool hasBandTypes = doBandScrape;
        if (hasBandTypes)
        {
            bandSpool = BandSpoolWriterFactory.Create(_log, _bandPersistence, spoolDir);
            SetActiveBandSpool(bandSpool);
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
        async ValueTask OnSoloSongComplete(string songId, List<GlobalLeaderboardResult> results)
        {
            bool hasData = false;
            foreach (var result in results)
            {
                Interlocked.Add(ref totalRequests, result.Requests);
                Interlocked.Add(ref totalBytes, result.BytesReceived);

                _persistence.RegisterSnapshotReuseManifest(
                    scrapeId,
                    songId,
                    result.Instrument,
                    result.CompletenessManifest);

                if (result.EntriesCount == 0) continue;
                hasData = true;
                aggregates.IncrementSoloLeaderboardsWithData();

                if (result.Entries.Count > 0)
                {
                    if (useOnlineSoloWriter)
                    {
                        await _persistence.EnqueueOnlineSoloPageAsync(songId, result.Instrument, result.Entries, passCt)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        _persistence.EnqueueSpoolPage(songId, result.Instrument, result.Entries);
                    }

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
            if (hasData && persistedSongsWithData.TryAdd(songId, 0))
            {
                aggregates.IncrementSongsWithData();
                aggregates.AddChangedSongId(songId);
            }
        }

        // Band scrape: flat parallel page fetcher using SharedDopPool for
        // low-priority DOP gating.  Band requests share the same AIMD limiter
        // as solo but are capped to LowPriorityPercent when solo is active.
        Task? bandTask = null;
        BandPageFetcher? bandFetcher = null;
        CancellationTokenSource? bandTimeoutCts = null;
        if (bandInstruments.Count > 0 && bandSpool is not null)
        {
            var bandSongIds = scrapeRequests.Select(r => r.SongId).ToList();
            bandTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(passCt);
            bandFetcher = new BandPageFetcher(
                _globalScraper.Executor, _pool, bandSpool, _progress, _log, accessTokenProvider);
            bandTask = bandFetcher.FetchAllAsync(
                bandSongIds, bandInstruments, accessToken, callerAccountId,
                opts.MaxPagesPerLeaderboard, bandTimeoutCts.Token);
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
                onBandPageScraped: null,
                accessTokenProvider: accessTokenProvider);

        // Wait for solo only — band runs independently in the background.
        // Solo post-processing (flush, score changes, rankings) proceeds immediately.
            try
            {
                allResults = await soloTask;
            }
            catch
            {
                if (bandTask is not null && !bandTask.IsCompleted)
                {
                    _progress.SetSubOperation("cancelling_band_after_solo_failure");
                    bandTimeoutCts?.Cancel();
                    _log.LogWarning("Cancelling background band scrape because the solo scrape did not complete.");
                    await ObserveTimedOutBandTaskAsync(bandTask, passCt);
                    _globalScraper.ResetCdnState();
                }

                if (bandSpool is not null)
                {
                    await DisposeBandSpoolAsync(bandSpool);
                    bandSpool = null;
                }

                bandTimeoutCts?.Dispose();
                bandTimeoutCts = null;
                throw;
            }
            finally
            {
                _pool.EndHighPriorityPhase();
            }

            if (useOnlineSoloWriter)
            {
                _progress.SetSubOperation("draining_solo_writes");
                writerResults.Add(
                    await _persistence.DrainOnlineSoloWriterAsync(
                        _progress));
            }
            else
            {
                // ── Post-fetch bulk flush for solo: drop solo indexes → flush → recreate ──
                _progress.SetSubOperation("dropping_solo_indexes");
                _persistence.DropSoloIndexes(_progress);
                try
                {
                    _progress.SetSubOperation("flushing_solo");
                    writerResults.Add(await _persistence.FlushSpoolAsync(_progress));
                }
                finally
                {
                    _progress.SetSubOperation("creating_solo_indexes");
                    _persistence.CreateSoloIndexes(_progress);
                }
            }

            // ── Detect score changes for registered users (solo data only) ──
            _progress.SetSubOperation("detecting_score_changes");
            int totalScoreChanges = 0;
            if (registeredIds.Count > 0)
            {
                if (_persistence.WriteLegacyLiveLeaderboardDuringScrape)
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
                else
                {
                    _log.LogInformation("Skipping live-table score change detection because legacy live scrape writes are disabled; snapshot current-state rows remain authoritative.");
                }
            }

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

        if (accessTokenProvider?.RefreshCount > 0)
        {
            _log.LogInformation("Scrape run #{ScrapeId} refreshed its access token {RefreshCount} time(s) during page fetches.",
                scrapeId, accessTokenProvider.RefreshCount);
        }

        // Report entry counts per instrument
        if (_persistence.WriteLegacyLiveLeaderboardDuringScrape)
        {
            var counts = _persistence.GetEntryCounts();
            foreach (var (instrument, count) in counts)
                _log.LogInformation("  {Instrument}: {Count:N0} entries", instrument, count);
        }
        else
        {
            _log.LogInformation("Skipping legacy live leaderboard_entries count report because legacy live scrape writes are disabled.");
        }

        // ── Update leaderboard population from Epic's reported totalPages ──
        _progress.SetSubOperation("updating_population");
        var populationItems = new List<(string SongId, string Instrument, long TotalEntries)>();
        var epicReportedOver100Pages = false;
        foreach (var (_, results) in allResults)
            foreach (var r in results)
                if (r.ReportedTotalPages > 0)
                {
                    epicReportedOver100Pages |= r.ReportedTotalPages > 100;
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

        if (doSoloScrape
            && (_persistence.WritePublishedScopeSources
                || _persistence.EnforceScopeCompletenessManifests)
            && !writerResults.Any(static result => !result.IsSuccess))
        {
            var expectedPairs = BuildExpectedSoloLeaderboardPairs(scrapeRequests);
            var coverageStopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                soloCoverageResult = _persistence.RecordLeaderboardScopeCoverage(
                    scrapeId,
                    allResults.Values.SelectMany(static results => results),
                    expectedPairs);
                coverageStopwatch.Stop();
                _log.LogInformation(
                    "Recorded published-source coverage for scrape {ScrapeId}: expected={Expected:N0}, observed={Observed:N0}, persisted={Persisted:N0}, missing={Missing:N0}, incomplete={Incomplete:N0}, elapsed={Elapsed}.",
                    scrapeId,
                    soloCoverageResult.ExpectedScopeCount,
                    soloCoverageResult.ObservedScopeCount,
                    soloCoverageResult.PersistedScopeCount,
                    soloCoverageResult.MissingScopeCount,
                    soloCoverageResult.IncompleteScopeCount,
                    coverageStopwatch.Elapsed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                coverageStopwatch.Stop();
                soloCoverageFailure = ex;
                _log.LogError(
                    ex,
                    "Failed to persist solo scope coverage/manifests for scrape {ScrapeId}; band work will be observed before rejecting the candidate.",
                    scrapeId);
            }
        }

        // ── Band: await completion and flush (runs in background during solo post-processing) ──
        if (bandTask is not null && bandSpool is not null)
        {
            var shouldFlushBand = true;
            try
            {
                _progress.SetSubOperation("awaiting_band");
                var bandAwaitTimeoutSeconds = Math.Max(0, opts.BandAwaitTimeoutAfterSoloSeconds);
                if (bandAwaitTimeoutSeconds > 0)
                {
                    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(bandAwaitTimeoutSeconds), passCt);
                    var completed = await Task.WhenAny(
                        bandTask,
                        timeoutTask);

                    if (ReferenceEquals(completed, timeoutTask))
                    {
                        await timeoutTask;
                        if (!bandTask.IsCompleted)
                        {
                            shouldFlushBand = false;
                            _progress.SetSubOperation("skipping_band_after_timeout");
                            bandTimeoutCts?.Cancel();
                            _log.LogWarning(
                                "Band scrape did not complete within {TimeoutSeconds}s after solo completion; skipping band flush for this pass so solo-derived phases can continue.",
                                bandAwaitTimeoutSeconds);
                            await ObserveTimedOutBandTaskAsync(bandTask, passCt);
                            _globalScraper.ResetCdnState();
                        }
                    }
                }

                if (shouldFlushBand)
                    await bandTask;

                Interlocked.Add(ref totalRequests, (int)Interlocked.Read(ref bandFetcher!.TotalRequests));
                Interlocked.Add(ref totalBytes, Interlocked.Read(ref bandFetcher.TotalBytes));

                if (shouldFlushBand)
                {
                    _progress.SetSubOperation("dropping_band_indexes");
                    _persistence.DropBandIndexes(_progress);
                    try
                    {
                        _progress.SetSubOperation("flushing_band");
                        bandSpool.Complete();
                        _log.LogInformation("Flushing band spool: {Records:N0} pages, {Entries:N0} entries...",
                            bandSpool.RecordCount, bandSpool.EntryCount);
                        writerResults.Add(await Task.Run(() => bandSpool.FlushAll(
                            maxBatchPages: 64,
                            onProgress: ReportBandSpoolFlushProgress)));
                    }
                    finally
                    {
                        _progress.SetSubOperation("creating_band_indexes");
                        _persistence.CreateBandIndexes(_progress);
                    }

                    _log.LogInformation("Band flush complete.");
                }
            }
            catch (OperationCanceledException) when (bandTimeoutCts?.IsCancellationRequested == true && !passCt.IsCancellationRequested)
            {
                _log.LogWarning("Band scrape cancelled after timeout; continuing with solo-first progression.");
            }
            finally
            {
                await DisposeBandSpoolAsync(bandSpool);
                bandSpool = null;
                bandTimeoutCts?.Dispose();
            }
        }

        ScopeManifestPersistenceResult? bandManifestResult = null;
        if (doBandScrape
            && bandFetcher is not null
            && (_persistence.WritePublishedScopeSources
                || _persistence.EnforceScopeCompletenessManifests))
        {
            var expectedBandPairs = scrapeRequests
                .SelectMany(request => bandInstruments.Select(
                    instrument => (request.SongId, Instrument: instrument)))
                .ToArray();
            bandManifestResult = _persistence.RecordScopeCompletenessManifests(
                scrapeId,
                bandFetcher.ScopeManifests,
                expectedBandPairs);
            _log.LogInformation(
                "Recorded band scope manifests for scrape {ScrapeId}: expected={Expected:N0}, observed={Observed:N0}, persisted={Persisted:N0}, missing={Missing:N0}, incomplete={Incomplete:N0}.",
                scrapeId,
                bandManifestResult.ExpectedScopeCount,
                bandManifestResult.ObservedScopeCount,
                bandManifestResult.PersistedScopeCount,
                bandManifestResult.MissingScopeCount,
                bandManifestResult.IncompleteScopeCount);
        }

        var failedWriterResults = writerResults
            .Where(static result => !result.IsSuccess)
            .ToArray();
        if (failedWriterResults.Length > 0)
        {
            _persistence.Meta.RecordScrapeWriterFailures(scrapeId, failedWriterResults);
            var writerException = new ScrapeWriterException(scrapeId, failedWriterResults);
            if (_persistence.RequireSuccessfulScrapeWriters)
            {
                _persistence.Meta.FailScrapeRun(scrapeId, "writer", writerException.Message);
                throw writerException;
            }

            _log.LogCritical(
                writerException,
                "Scrape {ScrapeId} has retained writer failures, but strict writer gating is disabled by rollback flag.",
                scrapeId);
        }

        if (soloCoverageFailure is not null)
        {
            _persistence.Meta.FailScrapeRun(
                scrapeId,
                "scope_completeness",
                soloCoverageFailure.Message);
            throw new InvalidOperationException(
                $"Scrape {scrapeId} solo scope coverage persistence failed.",
                soloCoverageFailure);
        }

        if (soloCoverageResult is not null && !soloCoverageResult.IsComplete)
        {
            var exception = new InvalidOperationException(
                $"Scrape {scrapeId} published-source coverage is incomplete: " +
                $"expected={soloCoverageResult.ExpectedScopeCount}, " +
                $"observed={soloCoverageResult.ObservedScopeCount}, " +
                $"persisted={soloCoverageResult.PersistedScopeCount}, " +
                $"missing={soloCoverageResult.MissingScopeCount}, " +
                $"incomplete={soloCoverageResult.IncompleteScopeCount}.");
            _persistence.Meta.FailScrapeRun(
                scrapeId,
                "scope_completeness",
                exception.Message);
            throw exception;
        }

        if (_persistence.EnforceScopeCompletenessManifests
            && bandManifestResult is not null
            && !bandManifestResult.IsComplete)
        {
            var exception = new InvalidOperationException(
                $"Scrape {scrapeId} band scope manifests are incomplete: " +
                $"expected={bandManifestResult.ExpectedScopeCount}, " +
                $"observed={bandManifestResult.ObservedScopeCount}, " +
                $"persisted={bandManifestResult.PersistedScopeCount}, " +
                $"missing={bandManifestResult.MissingScopeCount}, " +
                $"incomplete={bandManifestResult.IncompleteScopeCount}.");
            _persistence.Meta.FailScrapeRun(
                scrapeId,
                "band_scope_completeness",
                exception.Message);
            throw exception;
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
            EpicReportedOver100Pages = epicReportedOver100Pages,
            LeaderboardScrapeCompleted = true,
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
            EpicReportedOver100Pages = epicReportedOver100Pages,
        };
    }

    private async Task ObserveTimedOutBandTaskAsync(Task bandTask, CancellationToken passCt)
    {
        try
        {
            await bandTask.WaitAsync(TimeSpan.FromSeconds(10), passCt);
            _log.LogInformation("Band scrape acknowledged timeout cancellation before spool disposal.");
        }
        catch (OperationCanceledException) when (!passCt.IsCancellationRequested)
        {
            _log.LogWarning("Band scrape cancelled after timeout; continuing with solo-first progression.");
        }
        catch (TimeoutException)
        {
            _log.LogWarning("Timed-out band scrape did not stop within 10s after cancellation; disposing band spool and continuing with solo-first progression.");
        }
        catch (Exception ex) when (!passCt.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Band scrape stopped after timeout with an exception; continuing with solo-first progression.");
        }
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

    internal static IReadOnlyList<(string SongId, string Instrument)> BuildExpectedSoloLeaderboardPairs(
        IEnumerable<GlobalLeaderboardScraper.SongScrapeRequest> requests)
    {
        var pairs = new HashSet<(string SongId, string Instrument)>();
        foreach (var request in requests)
        {
            if (string.IsNullOrWhiteSpace(request.SongId))
                continue;

            foreach (var instrument in request.Instruments)
            {
                if (string.IsNullOrWhiteSpace(instrument) || IsBandInstrument(instrument))
                    continue;

                pairs.Add((request.SongId, instrument));
            }
        }

        return pairs.ToArray();
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

    public async ValueTask CleanupActiveBandSpoolAsync()
    {
        SpoolWriter<BandLeaderboardEntry>? spool;
        lock (_activeBandSpoolLock)
        {
            spool = _activeBandSpool;
            _activeBandSpool = null;
        }

        if (spool is null)
            return;

        _log.LogInformation("Best-effort cleanup disposing active band spool writer.");
        try
        {
            await spool.DisposeAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Best-effort cleanup failed while disposing active band spool writer.");
        }
    }

    private void SetActiveBandSpool(SpoolWriter<BandLeaderboardEntry> spool)
    {
        lock (_activeBandSpoolLock)
            _activeBandSpool = spool;
    }

    private async ValueTask DisposeBandSpoolAsync(SpoolWriter<BandLeaderboardEntry> spool)
    {
        try
        {
            await spool.DisposeAsync();
        }
        finally
        {
            lock (_activeBandSpoolLock)
            {
                if (ReferenceEquals(_activeBandSpool, spool))
                    _activeBandSpool = null;
            }
        }
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
