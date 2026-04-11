using System.Diagnostics.CodeAnalysis;
using FortniteFestival.Core;
using FortniteFestival.Core.Services;
using FortniteFestival.Core.Persistence;
using FSTService.Api;
using FSTService.Auth;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Options;

namespace FSTService;

/// <summary>
/// Background worker that continuously scrapes Fortnite Festival leaderboard scores.
///
/// Lifecycle:
///   1. Ensure authenticated (device auth → refresh → device code setup)
///   2. Initialize FestivalService (song catalog, images)
///   3. Scrape global leaderboards for all songs (V1 alltime)
///   4. Persist to sharded SQLite DBs, resolve names
///   5. Sleep for configured interval
///   6. Repeat
/// </summary>
public sealed class ScraperWorker : BackgroundService
{
    private readonly TokenManager _tokenManager;
    private readonly GlobalLeaderboardScraper _globalScraper;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly FestivalService _festivalService;
    private readonly StartupInitializer _dbInitializer;
    private readonly ScrapeOrchestrator _scrapeOrchestrator;
    private readonly PostScrapeOrchestrator _postScrapeOrchestrator;
    private readonly BackfillOrchestrator _backfillOrchestrator;
    private readonly CyclicalSongMachine _cyclicalMachine;
    private readonly PathGenerator _pathGenerator;
    private readonly IPathDataStore _pathDataStore;
    private readonly SongsCacheService _songsCache;
    private readonly ResponseCacheService _playerCache;
    private readonly ResponseCacheService _leaderboardAllCache;
    private readonly ScrapeLifecycleNotifier _lifecycle;
    private readonly ScrapeTimePrecomputer _precomputer;
    private readonly ScrapeProgressTracker _progress;
    private readonly UserSyncProgressTracker _syncTracker;
    private readonly IOptions<ScraperOptions> _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ScraperWorker> _log;
    private readonly System.Text.Json.JsonSerializerOptions _jsonOpts;

    /// <summary>Background song sync task — stored so we can observe failures.</summary>
    private Task? _backgroundSyncTask;

    public ScraperWorker(
        TokenManager tokenManager,
        GlobalLeaderboardScraper globalScraper,
        GlobalLeaderboardPersistence persistence,
        FestivalService festivalService,
        StartupInitializer dbInitializer,
        ScrapeOrchestrator scrapeOrchestrator,
        PostScrapeOrchestrator postScrapeOrchestrator,
        BackfillOrchestrator backfillOrchestrator,
        CyclicalSongMachine cyclicalMachine,
        PathGenerator pathGenerator,
        IPathDataStore IPathDataStore,
        SongsCacheService songsCache,
        [FromKeyedServices("PlayerCache")] ResponseCacheService playerCache,
        [FromKeyedServices("LeaderboardAllCache")] ResponseCacheService leaderboardAllCache,
        ScrapeLifecycleNotifier lifecycle,
        ScrapeTimePrecomputer precomputer,
        ScrapeProgressTracker progress,
        UserSyncProgressTracker syncTracker,
        IOptions<ScraperOptions> options,
        IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions,
        IHostApplicationLifetime lifetime,
        ILogger<ScraperWorker> log)
    {
        _tokenManager = tokenManager;
        _globalScraper = globalScraper;
        _persistence = persistence;
        _festivalService = festivalService;
        _dbInitializer = dbInitializer;
        _scrapeOrchestrator = scrapeOrchestrator;
        _postScrapeOrchestrator = postScrapeOrchestrator;
        _backfillOrchestrator = backfillOrchestrator;
        _cyclicalMachine = cyclicalMachine;
        _pathGenerator = pathGenerator;
        _pathDataStore = IPathDataStore;
        _songsCache = songsCache;
        _playerCache = playerCache;
        _leaderboardAllCache = leaderboardAllCache;
        _lifecycle = lifecycle;
        _precomputer = precomputer;
        _progress = progress;
        _syncTracker = syncTracker;
        _options = options;
        _jsonOpts = jsonOptions.Value.SerializerOptions;
        _lifetime = lifetime;
        _log = log;
    }

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "ScraperWorker failed with an unhandled exception.");
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;

        // Wait for DatabaseInitializer to finish (DBs + song catalog)
        await _dbInitializer.WaitForReadyAsync(stoppingToken);
        _log.LogInformation("Song catalog loaded. {SongCount} songs available for API.",
            _festivalService.Songs.Count);

        // Start the cyclical song machine so callers (post-scrape, backfill,
        // track endpoint) can attach at any time.
        _cyclicalMachine.Start(stoppingToken);

        // Pre-warm the rankings cache for registered users in the background so
        // that the scrape loop starts immediately. The cache TTL is 5 min, so the
        // worst case for API requests is a single on-demand CTE query. A 2-minute
        // timeout prevents unbounded blocking on large user counts.
        if (_persistence.GetInstrumentKeys().Count > 0)
        {
            var registeredIds = _persistence.Meta.GetRegisteredAccountIds();
            if (registeredIds.Count > 0)
                await _persistence.PreWarmRankingsCacheAsync(
                    registeredIds, TimeSpan.FromMinutes(2), stoppingToken);
        }

        // Precomputed API responses are now served from PostgreSQL.
        // No disk load needed — data persists across restarts in the api_response_cache table.
        {
            if (_precomputer.Count == 0)
                _log.LogInformation("No precomputed responses in RAM buffer (served from PostgreSQL).");
            PrimeSongsCache(); // Rebuild with population tiers
        }

        // --api-only mode: skip all background work, let the API serve requests
        if (opts.ApiOnly)
        {
            _log.LogInformation("Running in --api-only mode. Background scraping disabled. API is active.");
            // Keep the worker alive (but idle) so the host doesn't shut down
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { /* normal shutdown */ }
            return;
        }

        // --setup mode: only do device code auth, then exit
        if (opts.SetupOnly)
        {
            _log.LogInformation("Running in --setup mode (device code authentication only).");
            var ok = await _tokenManager.PerformDeviceCodeSetupAsync(stoppingToken);
            _log.LogInformation(ok ? "Setup complete! You can now run the service normally."
                                   : "Setup failed. Please try again.");
            return;
        }

        _log.LogInformation("ScraperWorker starting. Interval={Interval}, DOP={Dop}",
            opts.ScrapeInterval, opts.DegreeOfParallelism);

        // Ensure we have a valid auth session before entering the loop
        if (!await EnsureAuthenticatedAsync(stoppingToken))
        {
            return;
        }

        // --test mode: fetch one song and exit
        if (!string.IsNullOrEmpty(opts.TestSongQuery))
        {
            await RunSingleSongTestAsync(_festivalService, opts, stoppingToken);
            return;
        }

        // --resolve-only mode: skip scraping, just resolve unresolved account names
        if (opts.ResolveOnly)
        {
            await RunResolveOnlyAsync(stoppingToken);
            return;
        }

        // --backfill-only mode: skip scraping, just run backfill enrichment for registered users
        if (opts.BackfillOnly)
        {
            _log.LogInformation("Running in --backfill-only mode. Enriching existing entries with rank/percentile.");

            // ── DIAGNOSTIC: V2 lookup for #1 player to check if percentile is returned ──
            var diagToken = await _tokenManager.GetAccessTokenAsync(stoppingToken);
            var diagCaller = _tokenManager.AccountId!;
            if (diagToken is not null)
            {
                try
                {
                    // #1 Guitar player for song 092c2537 (popular song)
                    var diagEntry = await _globalScraper.LookupAccountAsync(
                        "092c2537-54ed-4963-9f91-873219ad5e74", "Solo_Guitar",
                        "e408c4613c8f4da5907090b390bda80c", diagToken, diagCaller, ct: stoppingToken);
                    if (diagEntry is not null)
                        _log.LogWarning("DIAG: #1 player V2 → Rank={Rank}, Percentile={Percentile}, Score={Score}",
                            diagEntry.Rank, diagEntry.Percentile, diagEntry.Score);
                    else
                        _log.LogWarning("DIAG: #1 player V2 → null (no entry)");
                }
                catch (Exception ex) { _log.LogWarning(ex, "DIAG: V2 lookup failed"); }
            }

            await _backfillOrchestrator.RunBackfillAsync(_festivalService, stoppingToken);
            _log.LogInformation("Backfill enrichment complete.");
            return;
        }

        // Start background song catalog refresh (every 15 minutes)
        // This runs independently of scraping — new songs get added to the DB
        // but won't be included in an already-running scrape pass.
        _backgroundSyncTask = BackgroundSongSyncLoopAsync(_festivalService, opts.SongSyncInterval, stoppingToken);

        // Register next-rank-update provider so sync completion messages include
        // an estimated time for global rankings recalculation.
        DateTime? lastScrapeEndUtc = null;
        _syncTracker.SetNextRankUpdateProvider(() =>
        {
            if (lastScrapeEndUtc is null) return null;
            return lastScrapeEndUtc.Value + opts.ScrapeInterval;
        });

        // Main scrape loop
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunScrapePassAsync(_festivalService, opts, stoppingToken);
            lastScrapeEndUtc = DateTime.UtcNow;

            // Phase-selective flags only affect the first (launch) pass.
            // After it completes, revert to the full pipeline for subsequent cycles.
            if (opts.EnabledPhases != ScrapePhase.None)
            {
                _log.LogInformation("Launch phases complete ({Phases}). Reverting to full pipeline for subsequent cycles.",
                    ScrapePhaseResolver.Format(opts.ResolvedPhases));
                opts.EnabledPhases = ScrapePhase.None;
            }

            if (opts.RunOnce)
            {
                _log.LogInformation("--once: scrape + resolve pass complete. Exiting.");
                break;
            }

            _log.LogInformation("Next scrape in {Interval}. Sleeping...", opts.ScrapeInterval);
            try
            {
                await Task.Delay(opts.ScrapeInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _log.LogInformation("ScraperWorker stopping.");

        // Observe background task to surface any unhandled exceptions
        if (_backgroundSyncTask is not null)
        {
            try { await _backgroundSyncTask; }
            catch (OperationCanceledException) { /* expected on shutdown */ }
            catch (Exception ex) { _log.LogError(ex, "Background song sync task faulted."); }
        }
    }

    // ─── Auth helpers ───────────────────────────────────────────

    /// <summary>
    /// Periodically re-syncs the song catalog from Epic on clock-aligned
    /// 15-minute boundaries (:00, :15, :30, :45).
    /// Runs as a fire-and-forget background task. New songs are persisted but do
    /// not affect any scrape pass that is already in progress.
    /// </summary>
    private async Task BackgroundSongSyncLoopAsync(
        FestivalService service, TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Sleep until the next clock-aligned boundary
            var now = DateTime.UtcNow;
            var intervalTicks = interval.Ticks;
            var nextTick = new DateTime((now.Ticks / intervalTicks + 1) * intervalTicks, DateTimeKind.Utc);
            var delay = nextTick - now;

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var before = service.Songs.Count;
                await service.SyncSongsAsync();
                var after = service.Songs.Count;
                if (after > before)
                {
                    _log.LogInformation("Song catalog refresh: {NewCount} new song(s) discovered ({Total} total).",
                        after - before, after);
                    PrimeSongsCache();
                    _persistence.InvalidateTotalSongCount();
                }
                else
                    _log.LogDebug("Song catalog refresh: {Total} songs in catalog (no changes).", after);

                // Fire-and-forget path generation for new/changed songs
                _ = TryGeneratePathsAsync(service, force: false, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Background song sync failed. Will retry at next interval.");
            }
        }
    }

    private async Task<bool> EnsureAuthenticatedAsync(CancellationToken ct)
    {
        var accessToken = await _tokenManager.GetAccessTokenAsync(ct);
        if (accessToken is not null)
            return true;

        _log.LogWarning("No stored credentials. Running interactive device code setup...");
        var ok = await _tokenManager.PerformDeviceCodeSetupAsync(ct);
        if (!ok)
        {
            _log.LogError("Device code setup failed. Cannot start scraping. Exiting.");
            return false;
        }
        return true;
    }

    // ─── Resolve-only mode ──────────────────────────────────────

    /// <summary>
    /// Skip scraping entirely.  Resolve display names for any account IDs
    /// already stored in the meta DB with LastResolved = NULL, then exit.
    /// </summary>
    private async Task RunResolveOnlyAsync(CancellationToken ct)
    {
        var unresolvedCount = _persistence.Meta.GetUnresolvedAccountCount();
        _log.LogInformation("--resolve-only: {Count} unresolved account(s) in meta DB.", unresolvedCount);

        if (unresolvedCount == 0)
        {
            _log.LogInformation("Nothing to resolve. Exiting.");
            return;
        }

        _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.ResolvingNames);
        try
        {
            var resolved = await _postScrapeOrchestrator.ResolveNamesAsync(maxConcurrency: 8, ct);
            _log.LogInformation("--resolve-only complete. {Resolved} account(s) resolved.", resolved);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Account name resolution failed.");
        }
    }

    private static IReadOnlyList<string> GetEnabledInstruments(ScraperOptions opts)
        => ScrapeOrchestrator.GetEnabledInstruments(opts);

    // ─── Scrape pass (V1 alltime global) ────────────────────────

    /// <summary>
    /// Scrape all songs via V1 alltime global leaderboards.
    /// Delegates core scraping to <see cref="ScrapeOrchestrator"/>, then
    /// runs post-scrape enrichment and backfill via downstream orchestrators.
    /// </summary>
    private async Task RunScrapePassAsync(
        FestivalService service,
        ScraperOptions opts,
        CancellationToken ct)
    {
        var processMemMb = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
        _log.LogInformation("Starting scrape pass... (Process memory: {MemoryMB} MB)", processMemMb);

        var resolvedPhases = opts.ResolvedPhases;
        if (resolvedPhases != ScrapePhase.All)
            _log.LogInformation("Phase-selective mode: {Phases}", ScrapePhaseResolver.Format(resolvedPhases));

        // Stale precomputed data (from last scrape) is served during the scrape pass.
        // PrecomputeAllAsync at post-scrape overwrites entries atomically, so we don't
        // need to invalidate here. This avoids an 8+ second cold-start penalty for the
        // first API request when the service restarts mid-scrape.

        var accessToken = await _tokenManager.GetAccessTokenAsync(ct);
        if (accessToken is null)
        {
            _log.LogError("Cannot obtain access token. Skipping this pass.");
            return;
        }

        // Re-sync the song catalog in case new songs appeared
        await service.SyncSongsAsync();
        _persistence.InvalidateTotalSongCount();

        // Make new songs visible immediately — SongsCacheService is independent
        // of the ResponseCacheService freeze, so this doesn't conflict with scrape
        // atomicity. Without this, new songs are invisible until the scrape completes.
        PrimeSongsCache();

        // Fire-and-forget path generation (runs in parallel with the scrape)
        var pathGenTask = TryGeneratePathsAsync(service, force: false, ct);

        // ── Core scrape: delegate to ScrapeOrchestrator ──
        // Freeze all response caches so API consumers see consistent (stale) data
        // throughout the scrape + post-scrape enrichment + precomputation cycle.
        _lifecycle.ScrapeStarting();

        bool anyScrapePhase = resolvedPhases.HasFlag(ScrapePhase.SoloScrape)
                           || resolvedPhases.HasFlag(ScrapePhase.BandScrape);

        ScrapePassResult? result = null;
        if (anyScrapePhase)
        {
            try
            {
                result = await _scrapeOrchestrator.RunAsync(
                    accessToken, _tokenManager.AccountId!, service, ct);
            }
            catch (CdnBlockedException ex)
            {
                _log.LogError(ex,
                    "CDN block escaped to scrape pass level (wire sends: {WireSends}, blocks: {Blocks}). " +
                    "Partial data from this pass was already persisted via pipelined writers.",
                    _globalScraper.Executor.TotalHttpSends, _globalScraper.Executor.CdnBlocksDetected);
                _lifecycle.ScrapeCompleted();
                return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning(
                    "Scrape pass timed out after {TimeoutMinutes} minutes. " +
                    "Partial data from this pass was already persisted. Will retry next pass.",
                    opts.ScrapePassTimeoutMinutes);
                _lifecycle.ScrapeCompleted();
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex,
                    "Scrape pass failed with a non-CDN exception. Partial data may have been staged. " +
                    "Will retry next pass.");
                _lifecycle.ScrapeCompleted();
                return;
            }
        }
        else
        {
            _log.LogInformation("Scrape phases not requested. Skipping ScrapeOrchestrator.");
        }

        // Build a minimal context when scrape was skipped (post-scrape phases
        // still need registered IDs, scrape requests, etc.)
        var ctx = result?.Context ?? new ScrapePassContext
        {
            AccessToken = accessToken,
            CallerAccountId = _tokenManager.AccountId!,
            RegisteredIds = _persistence.Meta.GetRegisteredAccountIds(),
            Aggregates = new Persistence.GlobalLeaderboardPersistence.PipelineAggregates(),
            ScrapeRequests = service.Songs
                .Where(s => s.track?.su is not null)
                .Select(s => new Scraping.GlobalLeaderboardScraper.SongScrapeRequest
                {
                    SongId = s.track.su,
                    Instruments = Scraping.ScrapeOrchestrator.GetEnabledInstruments(opts),
                    Label = s.track.tt,
                })
                .ToList(),
            DegreeOfParallelism = opts.DegreeOfParallelism,
        };

        // Observe the path generation task that ran in parallel with the scrape
        try { await pathGenTask; }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (Exception ex) { _log.LogError(ex, "Path generation task faulted during scrape pass."); }

        // ── Post-pass: enrichment, refresh, backfill, history recon, cleanup ──
        try
        {
            await _postScrapeOrchestrator.RunAsync(ctx, service, resolvedPhases, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Post-scrape orchestration failed. Finalizing pass with stale data.");
        }

        PrimeSongsCache();

        // Unfreeze all response caches and invalidate — API consumers now see fresh data atomically.
        _lifecycle.ScrapeCompleted();

        var endMemMb = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
        _log.LogInformation("Scrape pass complete. (Process memory: {MemoryMB} MB)", endMemMb);

        _progress.EndPass();
    }

    // ─── Path generation ──────────────────────────────────────

    /// <summary>
    /// Generates optimal paths and max attainable scores for new/changed songs.
    /// Downloads encrypted MIDI from Epic, decrypts, runs CHOpt, stores results.
    /// Safe to call as fire-and-forget — errors are logged but don't block scraping.
    /// </summary>
    [ExcludeFromCodeCoverage] // Error/persist paths fully tested via PathGeneratorOrchestrationTests; Coverlet async state machine gap on catch block
    internal async Task TryGeneratePathsAsync(FestivalService service, bool force, CancellationToken ct)
    {
        var opts = _options.Value;
        if (!opts.EnablePathGeneration)
            return;

        try
        {
            var songs = service.Songs.Where(s => s.track?.su is not null && !string.IsNullOrEmpty(s.track.mu)).ToList();
            if (songs.Count == 0) return;

            // Load existing path generation state to detect changes
            var existingState = _pathDataStore.GetPathGenerationState();

            var requests = songs.Select(s =>
            {
                existingState.TryGetValue(s.track.su, out var state);
                return new PathGenerator.SongPathRequest(
                    s.track.su,
                    s.track.tt ?? s.track.su,
                    s.track.an ?? "Unknown",
                    s.track.mu,
                    s.lastModified == DateTime.MinValue ? null : s.lastModified,
                    state.Hash,
                    state.LastModified);
            }).ToList();

            var ownsProgress = _progress.BeginPathGeneration(requests.Count);

            var results = await _pathGenerator.GeneratePathsAsync(requests, force, ct);

            if (results.Count == 0)
            {
                if (ownsProgress) _progress.EndPathGeneration();
                return;
            }

            // Persist max scores to the Songs DB
            foreach (var result in results)
            {
                var scores = new SongMaxScores
                {
                    GeneratedAt = DateTime.UtcNow.ToString("o"),
                    CHOptVersion = "1.10.3", // TODO: detect from binary
                };
                foreach (var pr in result.Results.Where(r => r.Difficulty == "expert"))
                    scores.SetByInstrument(pr.Instrument, pr.MaxScore);

                // Find the song's lastModified to store alongside
                var song = songs.FirstOrDefault(s => s.track.su == result.SongId);
                var songLastMod = song?.lastModified is { } lm && lm != DateTime.MinValue ? lm.ToString("o") : null;
                _pathDataStore.UpdateMaxScores(result.SongId, scores, result.DatFileHash, songLastMod);
            }

            PrimeSongsCache();
            if (ownsProgress) _progress.EndPathGeneration();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _progress.EndPathGeneration();
            _log.LogWarning(ex, "Path generation failed. Scraping continues unaffected.");
        }
    }

    // ─── Songs cache priming ────────────────────────────────────

    private void PrimeSongsCache()
    {
        try
        {
            _songsCache.Prime(_festivalService, _pathDataStore, _persistence.Meta, _precomputer, _jsonOpts);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to prime songs cache; will rebuild on next request.");
            _songsCache.Invalidate();
        }
    }

    // ─── Song test ───────────────────────────────────────────────

    private async Task RunSingleSongTestAsync(
        FestivalService service,
        ScraperOptions opts,
        CancellationToken ct)
    {
        // Support comma-separated queries: --test "Song A,Song B"
        var queries = opts.TestSongQuery!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        _log.LogInformation("Test mode. Searching for {Count} song(s): {Queries}",
            queries.Length, string.Join(", ", queries.Select(q => $"\"{q}\"")));

        // Resolve each query to a Song
        var matched = new List<Song>();
        foreach (var query in queries)
        {
            var match = service.Songs.FirstOrDefault(s =>
                s.track?.tt != null &&
                s.track.tt.Contains(query, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                _log.LogError("No song matching \"{Query}\" found in catalog ({Total} songs).",
                    query, service.Songs.Count);
                continue;
            }

            _log.LogInformation("Found: \"{Title}\" by {Artist}  [id={SongId}]",
                match.track.tt, match.track.an, match.track.su);
            matched.Add(match);
        }

        if (matched.Count == 0)
        {
            _log.LogError("No songs matched. Exiting.");
            return;
        }

        var accessToken = await _tokenManager.GetAccessTokenAsync(ct);
        if (accessToken is null)
        {
            _log.LogError("Cannot obtain access token for test.");
            return;
        }

        var accountId = _tokenManager.AccountId!;

        // Build scrape requests — query all instruments for every song
        var scrapeRequests = matched.Select(song => new GlobalLeaderboardScraper.SongScrapeRequest
        {
            SongId = song.track.su,
            Instruments = GlobalLeaderboardScraper.AllInstruments,
            Label = song.track.tt,
        }).ToList();

        _log.LogInformation("Scraping {SongCount} song(s) across all instruments (DOP={Dop})...",
            scrapeRequests.Count, opts.DegreeOfParallelism);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var allResults = await _globalScraper.ScrapeManySongsAsync(
            scrapeRequests, accessToken, accountId, opts.DegreeOfParallelism, onSongComplete: null, ct,
            maxPages: opts.MaxPagesPerLeaderboard,
            sequential: opts.SequentialScrape,
            pageConcurrency: opts.PageConcurrency,
            songConcurrency: opts.SongConcurrency,
            validEntryTarget: opts.ValidEntryTarget);
        sw.Stop();

        // Grand summary
        int grandEntries = allResults.Values.SelectMany(r => r).Sum(r => r.Entries.Count);
        int grandRequests = allResults.Values.SelectMany(r => r).Sum(r => r.Requests);
        long grandBytes = allResults.Values.SelectMany(r => r).Sum(r => r.BytesReceived);

        _log.LogInformation(
            "All done. {Songs} songs, {Entries} total entries, {Requests} requests, {Bytes} bytes, {Elapsed:F1}s",
            allResults.Count, grandEntries, grandRequests, grandBytes, sw.Elapsed.TotalSeconds);

        // Per-song detail
        foreach (var song in matched)
        {
            if (!allResults.TryGetValue(song.track.su, out var results)) continue;

            _log.LogInformation("═══ {Title} by {Artist} ═══", song.track.tt, song.track.an);

            foreach (var result in results)
            {
                _log.LogInformation("── {Instrument}: {Count} entries, {Pages} pages ──",
                    result.Instrument, result.Entries.Count, result.TotalPages);

                foreach (var entry in result.Entries.Take(3))
                {
                    _log.LogInformation(
                        "    {AccountId}  Score={Score}  Accuracy={Accuracy}%  Stars={Stars}  FC={FC}",
                        entry.AccountId, entry.Score, entry.Accuracy,
                        entry.Stars, entry.IsFullCombo ? "YES" : "no");
                }

                if (result.Entries.Count > 3)
                    _log.LogInformation("    ... and {More} more entries", result.Entries.Count - 3);
            }
        }
    }
}
