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
    private readonly AccountNameResolver _nameResolver;
    private readonly PersonalDbBuilder _personalDbBuilder;
    private readonly ScoreBackfiller _backfiller;
    private readonly BackfillQueue _backfillQueue;
    private readonly PostScrapeRefresher _refresher;
    private readonly HistoryReconstructor _historyReconstructor;
    private readonly FirstSeenSeasonCalculator _firstSeenCalculator;
    private readonly FestivalService _festivalService;
    private readonly NotificationService _notifications;
    private readonly TokenVault _tokenVault;
    private readonly ScrapeProgressTracker _progress;
    private readonly IOptions<ScraperOptions> _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ScraperWorker> _log;

    public ScraperWorker(
        TokenManager tokenManager,
        GlobalLeaderboardScraper globalScraper,
        GlobalLeaderboardPersistence persistence,
        AccountNameResolver nameResolver,
        PersonalDbBuilder personalDbBuilder,
        ScoreBackfiller backfiller,
        BackfillQueue backfillQueue,
        PostScrapeRefresher refresher,
        HistoryReconstructor historyReconstructor,
        FirstSeenSeasonCalculator firstSeenCalculator,
        FestivalService festivalService,
        NotificationService notifications,
        TokenVault tokenVault,
        ScrapeProgressTracker progress,
        IOptions<ScraperOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<ScraperWorker> log)
    {
        _tokenManager = tokenManager;
        _globalScraper = globalScraper;
        _persistence = persistence;
        _nameResolver = nameResolver;
        _personalDbBuilder = personalDbBuilder;
        _backfiller = backfiller;
        _backfillQueue = backfillQueue;
        _refresher = refresher;
        _historyReconstructor = historyReconstructor;
        _firstSeenCalculator = firstSeenCalculator;
        _festivalService = festivalService;
        _notifications = notifications;
        _tokenVault = tokenVault;
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

        // Create the persistence layer
        var dbPath = Path.GetFullPath(opts.DatabasePath);
        var dbDir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
            Directory.CreateDirectory(dbDir);

        var persistence = new SqlitePersistence(dbPath);
        var service = new FestivalService(persistence);

        service.Log += msg => _log.LogInformation("[Core] {Message}", msg);

        // Initialize: fetch song catalog, images, load cached scores
        _log.LogInformation("Initializing song catalog...");
        await service.InitializeAsync();
        _log.LogInformation("Initialization complete. {SongCount} songs loaded.", service.Songs.Count);

        // --test mode: fetch one song and exit
        if (!string.IsNullOrEmpty(opts.TestSongQuery))
        {
            await RunSingleSongTestAsync(service, opts, stoppingToken);
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

            await RunBackfillPhaseAsync(service, stoppingToken);
            _log.LogInformation("Backfill enrichment complete.");
            return;
        }

        // Start background song catalog refresh (every 15 minutes)
        // This runs independently of scraping — new songs get added to the DB
        // but won't be included in an already-running scrape pass.
        _ = BackgroundSongSyncLoopAsync(service, opts.SongSyncInterval, stoppingToken);

        // Main scrape loop
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunScrapePassAsync(service, opts, stoppingToken);

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

                    // Keep the DI singleton (used by API) in sync
                    await _festivalService.InitializeAsync();
                }
                else
                    _log.LogDebug("Background song sync complete. {Total} songs (no changes).", after);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Background song sync failed. Will retry at next quarter-hour.");
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
            var resolved = await _nameResolver.ResolveNewAccountsAsync(maxConcurrency: 8, ct);
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

        // ── Post-pass: calculate FirstSeenSeason for new songs ──
        _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.CalculatingFirstSeen);

        // Recompute stored rank columns across all instrument DBs
        try
        {
            var rankUpdated = _persistence.RecomputeAllRanks();
            _log.LogInformation("Recomputed ranks across all instruments: {Count:N0} entries updated.", rankUpdated);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Rank recomputation failed. Stored ranks may be stale.");
        }

        try
        {
            var firstSeenToken = await _tokenManager.GetAccessTokenAsync(ct);
            if (firstSeenToken is not null)
            {
                var firstSeenCount = await _firstSeenCalculator.CalculateAsync(
                    service, firstSeenToken, _tokenManager.AccountId!,
                    opts.DegreeOfParallelism, ct);
                if (firstSeenCount > 0)
                    _log.LogInformation("Calculated FirstSeenSeason for {Count} song(s).", firstSeenCount);
            }
            else
            {
                _log.LogWarning("No access token for FirstSeenSeason calculation. Will retry next pass.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "FirstSeenSeason calculation failed. Will retry next pass.");
        }

        // ── Post-pass: resolve display names for new accounts ──
        _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.ResolvingNames);
        try
        {
            await _nameResolver.ResolveNewAccountsAsync(maxConcurrency: 8, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Account name resolution failed. Will retry next pass.");
        }

        // ── Post-pass: rebuild personal DBs for registered users with score changes ──
        if (aggregates.ChangedAccountIds.Count > 0)
        {
            _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.RebuildingPersonalDbs);
            try
            {
                var changedIds = new HashSet<string>(aggregates.ChangedAccountIds, StringComparer.OrdinalIgnoreCase);
                var rebuilt = _personalDbBuilder.RebuildForAccounts(changedIds, _persistence.Meta);
                if (rebuilt > 0)
                {
                    _log.LogInformation("Rebuilt {Count} personal DB(s) for users with score changes.", rebuilt);

                    // Notify connected clients their personal DBs have been updated
                    foreach (var changedId in changedIds)
                    {
                        try { await _notifications.NotifyPersonalDbReadyAsync(changedId); }
                        catch { /* best effort */ }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Personal DB rebuild failed. Will retry next pass.");
            }
        }

        // ── Post-pass: refresh stale/missing entries for registered users ──
        if (registeredIds.Count > 0)
        {
            _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.RefreshingRegisteredUsers);
            try
            {
                var seenSet = new HashSet<(string AccountId, string SongId, string Instrument)>(
                    aggregates.SeenRegisteredEntries);
                var chartedSongIds = scrapeRequests.Select(r => r.SongId).ToList();

                var refreshToken = await _tokenManager.GetAccessTokenAsync(ct);
                if (refreshToken is not null)
                {
                    var refreshed = await _refresher.RefreshAllAsync(
                        registeredIds, seenSet, chartedSongIds,
                        refreshToken, _tokenManager.AccountId!,
                        opts.DegreeOfParallelism, ct);
                    if (refreshed > 0)
                        _log.LogInformation("Post-scrape refresh updated {Count} entries for registered users.", refreshed);
                }
                else
                {
                    _log.LogWarning("No access token for post-scrape refresh. Will retry next pass.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Post-scrape refresh failed. Will retry next pass.");
            }
        }

        // ── Post-pass: backfill missing scores for registered users ──
        await RunBackfillPhaseAsync(service, ct);

        // ── Post-pass: reconstruct score history for registered users (one-time) ──
        await RunHistoryReconPhaseAsync(ct);

        // ── Post-pass: clean up expired/revoked auth sessions ──
        try
        {
            var cleaned = _persistence.Meta.CleanupExpiredSessions(DateTime.UtcNow.AddDays(-7));
            if (cleaned > 0)
                _log.LogInformation("Cleaned up {Count} expired/revoked auth session(s).", cleaned);

            // Auto-unregister accounts whose sessions have all expired.
            // This stops backfill, personal DB builds, and post-scrape refreshes
            // for users who haven't opened the app since their refresh token expired.
            // They'll re-register automatically on their next login.
            var orphaned = _persistence.Meta.GetOrphanedRegisteredAccounts();
            foreach (var orphanedAccountId in orphaned)
            {
                var deviceIds = _persistence.Meta.UnregisterAccount(orphanedAccountId);
                foreach (var deviceId in deviceIds)
                {
                    var dbPath = _personalDbBuilder.GetPersonalDbPath(orphanedAccountId, deviceId);
                    if (File.Exists(dbPath))
                        File.Delete(dbPath);
                }

                _tokenVault.Revoke(orphanedAccountId);

                var displayName = _persistence.Meta.GetDisplayName(orphanedAccountId);
                _log.LogInformation(
                    "Auto-unregistered {DisplayName} ({AccountId}) — all sessions expired ({DeviceCount} device(s) removed).",
                    displayName ?? orphanedAccountId, orphanedAccountId, deviceIds.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Auth session cleanup failed. Will retry next pass.");
        }

        _progress.EndPass();
    }

    // ─── Backfill phase ─────────────────────────────────────────

    /// <summary>
    /// Run backfills for any queued accounts (from login/registration) and
    /// also resume any in-progress backfills that were interrupted.
    /// </summary>
    private async Task RunBackfillPhaseAsync(FestivalService service, CancellationToken ct)
    {
        // Drain any newly queued requests from login
        var queued = _backfillQueue.DrainAll();

        // Also pick up any pending/in_progress backfills from the DB
        var pending = _persistence.Meta.GetPendingBackfills();

        // Merge: create a distinct set of account IDs to process
        var accountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var req in queued) accountIds.Add(req.AccountId);
        foreach (var bf in pending) accountIds.Add(bf.AccountId);

        if (accountIds.Count == 0) return;

        _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.BackfillingScores);

        var accessToken = await _tokenManager.GetAccessTokenAsync(ct);
        if (accessToken is null)
        {
            _log.LogWarning("No access token available for backfill. Will retry next pass.");
            // Re-enqueue so they're not lost
            foreach (var id in accountIds) _backfillQueue.Enqueue(new BackfillRequest(id));
            return;
        }

        var callerAccountId = _tokenManager.AccountId!;

        foreach (var accountId in accountIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var found = await _backfiller.BackfillAccountAsync(
                    accountId, service, accessToken, callerAccountId,
                    _options.Value.DegreeOfParallelism, ct);

                // Rebuild personal DB for this user if we found new entries
                if (found > 0)
                {
                    try
                    {
                        _personalDbBuilder.RebuildForAccounts(
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { accountId },
                            _persistence.Meta);

                        // Notify connected clients that their personal DB is ready
                        await _notifications.NotifyBackfillCompleteAsync(accountId);
                        await _notifications.NotifyPersonalDbReadyAsync(accountId);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Personal DB rebuild after backfill failed for {AccountId}.", accountId);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Backfill failed for {AccountId}. Will retry next pass.", accountId);
            }
        }
    }

    // ─── History Reconstruction phase ───────────────────────────

    /// <summary>
    /// Run history reconstruction for registered users whose backfill is complete
    /// but whose history hasn't been reconstructed yet.
    /// </summary>
    private async Task RunHistoryReconPhaseAsync(CancellationToken ct)
    {
        // Find accounts with completed backfill but no completed history recon
        var registeredIds = _persistence.Meta.GetRegisteredAccountIds();
        if (registeredIds.Count == 0) return;

        var accountsToReconstruct = new List<string>();
        foreach (var accountId in registeredIds)
        {
            var backfillStatus = _persistence.Meta.GetBackfillStatus(accountId);
            if (backfillStatus?.Status != "complete") continue; // Backfill must finish first

            var reconStatus = _persistence.Meta.GetHistoryReconStatus(accountId);
            if (reconStatus?.Status == "complete") continue; // Already reconstructed

            accountsToReconstruct.Add(accountId);
        }

        if (accountsToReconstruct.Count == 0) return;

        _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.ReconstructingHistory);

        var accessToken = await _tokenManager.GetAccessTokenAsync(ct);
        if (accessToken is null)
        {
            _log.LogWarning("No access token available for history reconstruction. Will retry next pass.");
            return;
        }

        var callerAccountId = _tokenManager.AccountId!;

        // Discover season windows (cached after first call)
        IReadOnlyList<Persistence.SeasonWindowInfo> seasonWindows;
        try
        {
            seasonWindows = await _historyReconstructor.DiscoverSeasonWindowsAsync(
                accessToken, callerAccountId, ct);

            if (seasonWindows.Count == 0)
            {
                _log.LogWarning("No season windows discovered. Skipping history reconstruction.");
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Season window discovery failed. Will retry next pass.");
            return;
        }

        // Create a shared adaptive concurrency limiter so all users share the same
        // API call budget. This prevents overwhelming Epic's API when multiple users
        // are being reconstructed simultaneously.
        var dop = _options.Value.DegreeOfParallelism;
        int initialDop = Math.Max(1, dop / 2);
        int maxDop = dop * 2;
        using var sharedLimiter = new AdaptiveConcurrencyLimiter(initialDop, minDop: 2, maxDop: maxDop, _log);
        _progress.SetAdaptiveLimiter(sharedLimiter);

        _log.LogInformation(
            "Reconstructing history for {Count} account(s) in parallel with shared limiter (initial DOP={InitialDop}, max={MaxDop}).",
            accountsToReconstruct.Count, initialDop, maxDop);

        // Process all users in parallel. The shared AdaptiveConcurrencyLimiter
        // controls total API concurrency across all users, so there's no need
        // to limit how many users run concurrently at the task level.
        var userTasks = accountsToReconstruct.Select(accountId => Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var entries = await _historyReconstructor.ReconstructAccountAsync(
                    accountId, seasonWindows, accessToken, callerAccountId,
                    dop, sharedLimiter, ct);

                if (entries > 0)
                {
                    _log.LogInformation(
                        "History reconstruction for {AccountId}: {Entries} score history entries created.",
                        accountId, entries);

                    // Rebuild personal DB to include new history
                    try
                    {
                        _personalDbBuilder.RebuildForAccounts(
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { accountId },
                            _persistence.Meta);

                        // Notify connected clients that history recon is done and DB is ready
                        await _notifications.NotifyHistoryReconCompleteAsync(accountId);
                        await _notifications.NotifyPersonalDbReadyAsync(accountId);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Personal DB rebuild after history recon failed for {AccountId}.", accountId);
                    }
                }
            }
            catch (OperationCanceledException) { /* propagated via WhenAll */ }
            catch (Exception ex)
            {
                _log.LogError(ex, "History reconstruction failed for {AccountId}. Will retry next pass.", accountId);
                _persistence.Meta.FailHistoryRecon(accountId, ex.Message);
            }
        }, ct)).ToList();

        await Task.WhenAll(userTasks);
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
