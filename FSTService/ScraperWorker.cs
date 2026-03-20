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
///   4. Persist to sharded SQLite DBs, resolve names, rebuild personal DBs
///   5. Sleep for configured interval
///   6. Repeat
/// </summary>
public sealed class ScraperWorker : BackgroundService
{
    private readonly TokenManager _tokenManager;
    private readonly GlobalLeaderboardScraper _globalScraper;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly FestivalService _festivalService;
    private readonly PostScrapeOrchestrator _postScrapeOrchestrator;
    private readonly BackfillOrchestrator _backfillOrchestrator;
    private readonly PathGenerator _pathGenerator;
    private readonly PathDataStore _pathDataStore;
    private readonly ScrapeProgressTracker _progress;
    private readonly IOptions<ScraperOptions> _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ScraperWorker> _log;

    /// <summary>Background song sync task — stored so we can observe failures.</summary>
    private Task? _backgroundSyncTask;

    public ScraperWorker(
        TokenManager tokenManager,
        GlobalLeaderboardScraper globalScraper,
        GlobalLeaderboardPersistence persistence,
        FestivalService festivalService,
        PostScrapeOrchestrator postScrapeOrchestrator,
        BackfillOrchestrator backfillOrchestrator,
        PathGenerator pathGenerator,
        PathDataStore pathDataStore,
        ScrapeProgressTracker progress,
        IOptions<ScraperOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<ScraperWorker> log)
    {
        _tokenManager = tokenManager;
        _globalScraper = globalScraper;
        _persistence = persistence;
        _festivalService = festivalService;
        _postScrapeOrchestrator = postScrapeOrchestrator;
        _backfillOrchestrator = backfillOrchestrator;
        _pathGenerator = pathGenerator;
        _pathDataStore = pathDataStore;
        _progress = progress;
        _options = options;
        _lifetime = lifetime;
        _log = log;
    }

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

        // Always initialize the DI singleton so /api/songs works immediately
        await _festivalService.InitializeAsync();
        _log.LogInformation("Song catalog loaded. {SongCount} songs available for API.",
            _festivalService.Songs.Count);

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

        // Main scrape loop
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunScrapePassAsync(_festivalService, opts, stoppingToken);

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
                    _log.LogInformation("Background song sync: {NewCount} new song(s) discovered ({Total} total).",
                        after - before, after);
                }
                else
                    _log.LogDebug("Background song sync complete. {Total} songs (no changes).", after);

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

    // ─── Scrape pass (V1 alltime global) ────────────────────────

    /// <summary>
    /// Scrape all songs via V1 alltime global leaderboards.
    /// Persistence is pipelined: each song's results are written to SQLite
    /// as they arrive, overlapping disk I/O with ongoing network I/O.
    /// </summary>
    private async Task RunScrapePassAsync(
        FestivalService service,
        ScraperOptions opts,
        CancellationToken ct)
    {
        _log.LogInformation("Starting scrape pass...");

        var accessToken = await _tokenManager.GetAccessTokenAsync(ct);
        if (accessToken is null)
        {
            _log.LogError("Cannot obtain access token. Skipping this pass.");
            return;
        }

        var accountId = _tokenManager.AccountId!;

        // Re-sync the song catalog in case new songs appeared
        await service.SyncSongsAsync();

        // Fire-and-forget path generation (runs in parallel with the scrape)
        var pathGenTask = TryGeneratePathsAsync(service, force: false, ct);

        // Start scrape log entry
        var scrapeId = _persistence.Meta.StartScrapeRun();
        _log.LogInformation("Scrape run #{ScrapeId} started.", scrapeId);

        // Load registered account IDs for change detection
        var registeredIds = _persistence.Meta.GetRegisteredAccountIds();
        if (registeredIds.Count > 0)
            _log.LogInformation("{Count} registered user(s) will be tracked for score changes.", registeredIds.Count);

        // Build scrape requests: one per song, all enabled instruments.
        // We no longer filter by catalog difficulty metadata because
        // difficulty 0 is a valid value (not "uncharted"), and the API
        // returns real leaderboard data for every instrument on every song.
        var enabledInstruments = GetEnabledInstruments(opts);
        var scrapeRequests = service.Songs
            .Where(s => s.track?.su is not null)
            .Select(song => new GlobalLeaderboardScraper.SongScrapeRequest
            {
                SongId = song.track.su,
                Instruments = enabledInstruments,
                Label = song.track.tt,
            })
            .ToList();

        _log.LogInformation("Scraping {SongCount} songs across {InstrumentCount} instrument types (DOP={Dop})...",
            scrapeRequests.Count, enabledInstruments.Count, opts.DegreeOfParallelism);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ── Initialize progress tracker ──
        int totalLeaderboards = scrapeRequests.Sum(r => r.Instruments.Count);
        int cachedPages = LoadCachedPageEstimate(opts);
        _progress.BeginPass(totalLeaderboards, scrapeRequests.Count, cachedPages);

        // Tell the progress tracker how many leaderboards each instrument has
        var instrumentTotals = enabledInstruments
            .ToDictionary(i => i, _ => scrapeRequests.Count);
        _progress.SetInstrumentTotals(instrumentTotals);

        // ── Pipelined: per-instrument channel writers ──
        var aggregates = _persistence.StartWriters(ct);
        int totalRequests = 0;
        long totalBytes = 0;

        var allResults = await _globalScraper.ScrapeManySongsAsync(
            scrapeRequests, accessToken, accountId, opts.DegreeOfParallelism,
            onSongComplete: async (songId, results) =>
            {
                // Called concurrently from multiple song tasks.
                // Enqueue each instrument result into its dedicated channel —
                // no cross-instrument lock, back-pressure if the writer falls behind.
                bool hasData = false;
                foreach (var result in results)
                {
                    Interlocked.Add(ref totalRequests, result.Requests);
                    Interlocked.Add(ref totalBytes, result.BytesReceived);

                    if (result.Entries.Count == 0) continue;
                    hasData = true;

                    await _persistence.EnqueueResultAsync(result, registeredIds, ct);
                }
                if (hasData) aggregates.IncrementSongsWithData();
            },
            ct);

        // Wait for all per-instrument writers to drain
        await _persistence.DrainWritersAsync();

        sw.Stop();

        // Save page estimate for next run (still in Scraping phase, so current has the data)
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

        // Observe the path generation task that ran in parallel with the scrape
        try { await pathGenTask; }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (Exception ex) { _log.LogError(ex, "Path generation task faulted during scrape pass."); }

        // ── Post-pass: enrichment, refresh, backfill, history recon, cleanup ──
        var ctx = new ScrapePassContext
        {
            AccessToken = accessToken,
            CallerAccountId = _tokenManager.AccountId!,
            RegisteredIds = registeredIds,
            Aggregates = aggregates,
            ScrapeRequests = scrapeRequests,
            DegreeOfParallelism = opts.DegreeOfParallelism,
        };

        await _postScrapeOrchestrator.RunAsync(ctx, service, ct);

        await _backfillOrchestrator.RunBackfillAsync(service, ct);
        await _backfillOrchestrator.RunHistoryReconAsync(ct);

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

            _log.LogInformation("Path generation: checking {Count} songs for new/changed MIDI data.", requests.Count);

            var ownsProgress = _progress.BeginPathGeneration(requests.Count);

            var results = await _pathGenerator.GeneratePathsAsync(requests, force, ct);

            if (results.Count == 0)
            {
                _log.LogDebug("Path generation: no songs needed updating.");
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

            if (ownsProgress) _progress.EndPathGeneration();
            _log.LogInformation("Path generation complete: {Count} song(s) updated.", results.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _progress.EndPathGeneration();
            _log.LogWarning(ex, "Path generation failed. Scraping continues unaffected.");
        }
    }

    // ─── Cached page estimate ───────────────────────────────────

    private static int LoadCachedPageEstimate(ScraperOptions opts)
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

    private static void SaveCachedPageEstimate(ScraperOptions opts, int totalPages)
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
            scrapeRequests, accessToken, accountId, opts.DegreeOfParallelism, onSongComplete: null, ct);
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
