using FortniteFestival.Core.Scraping;
using FortniteFestival.Core.Services;
using FSTService.Api;
using FSTService.Auth;
using FSTService.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FSTService.Scraping;

/// <summary>
/// Orchestrates the post-scrape enrichment phases: parallel rank/firstSeen/nameRes,
/// refresh of registered users, and session cleanup.
/// Extracted from <see cref="ScraperWorker"/> to reduce its dependency count and
/// make each phase independently testable.
/// </summary>
public sealed class PostScrapeOrchestrator
{
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly FirstSeenSeasonCalculator _firstSeenCalculator;
    private readonly AccountNameResolver _nameResolver;
    private readonly PostScrapeRefresher _refresher;
    private readonly IServiceProvider _serviceProvider;
    private readonly HistoryReconstructor _historyReconstructor;
    private readonly SharedDopPool _pool;
    private readonly CyclicalSongMachine _cyclicalMachine;
    private readonly RivalsOrchestrator _rivalsOrchestrator;
    private readonly RankingsCalculator _rankingsCalculator;
    private readonly LeaderboardRivalsCalculator _leaderboardRivalsCalculator;
    private readonly NotificationService _notifications;
    private readonly TokenManager _tokenManager;
    private readonly ScrapeProgressTracker _progress;
    private readonly IPathDataStore _pathDataStore;
    private readonly ScrapeTimePrecomputer _precomputer;
    private readonly PostScrapeBandExtractor _bandExtractor;
    private readonly BandScrapePhase _bandScrapePhase;
    private readonly BandLeaderboardPersistence _bandPersistence;
    private readonly RegisteredBandProcessingOrchestrator? _registeredBandProcessingOrchestrator;
    private readonly BandSearchProjectionBuilder? _bandSearchProjectionBuilder;
    private readonly IOptions<ScraperOptions> _options;
    private readonly ILogger<PostScrapeOrchestrator> _log;

    public PostScrapeOrchestrator(
        GlobalLeaderboardPersistence persistence,
        FirstSeenSeasonCalculator firstSeenCalculator,
        AccountNameResolver nameResolver,
        PostScrapeRefresher refresher,
        IServiceProvider serviceProvider,
        HistoryReconstructor historyReconstructor,
        SharedDopPool pool,
        CyclicalSongMachine cyclicalMachine,
        RivalsOrchestrator rivalsOrchestrator,
        RankingsCalculator rankingsCalculator,
        LeaderboardRivalsCalculator leaderboardRivalsCalculator,
        NotificationService notifications,
        TokenManager tokenManager,
        ScrapeProgressTracker progress,
        IPathDataStore IPathDataStore,
        ScrapeTimePrecomputer precomputer,
        PostScrapeBandExtractor bandExtractor,
        BandScrapePhase bandScrapePhase,
        BandLeaderboardPersistence bandPersistence,
        IOptions<ScraperOptions> options,
        ILogger<PostScrapeOrchestrator> log,
        BandSearchProjectionBuilder? bandSearchProjectionBuilder,
        RegisteredBandProcessingOrchestrator? registeredBandProcessingOrchestrator = null)
    {
        _persistence = persistence;
        _firstSeenCalculator = firstSeenCalculator;
        _nameResolver = nameResolver;
        _refresher = refresher;
        _serviceProvider = serviceProvider;
        _historyReconstructor = historyReconstructor;
        _pool = pool;
        _cyclicalMachine = cyclicalMachine;
        _rivalsOrchestrator = rivalsOrchestrator;
        _rankingsCalculator = rankingsCalculator;
        _leaderboardRivalsCalculator = leaderboardRivalsCalculator;
        _notifications = notifications;
        _tokenManager = tokenManager;
        _progress = progress;
        _pathDataStore = IPathDataStore;
        _precomputer = precomputer;
        _bandExtractor = bandExtractor;
        _bandScrapePhase = bandScrapePhase;
        _bandPersistence = bandPersistence;
        _registeredBandProcessingOrchestrator = registeredBandProcessingOrchestrator;
        _bandSearchProjectionBuilder = bandSearchProjectionBuilder;
        _options = options;
        _log = log;
    }

    /// <summary>
    /// Run post-scrape phases gated by <paramref name="resolvedPhases"/>.
    /// When all phases are enabled this behaves identically to the original pipeline.
    /// </summary>
    public async Task RunAsync(ScrapePassContext ctx, FestivalService service, ScrapePhase resolvedPhases, CancellationToken ct)
    {
        // ── Solo enrichment ──
        if (resolvedPhases.HasFlag(ScrapePhase.SoloEnrichment))
            await RunPhaseAsync("Enrichment", () => RunEnrichmentAsync(ctx, service, ct));

        // ── Solo refresh registered users ──
        if (resolvedPhases.HasFlag(ScrapePhase.SoloRefreshUsers))
            await RunPhaseAsync("RefreshRegisteredUsers", () => RefreshRegisteredUsersAsync(ctx, ct));

        var expectedSnapshotPairs = BuildExpectedSnapshotPairs(ctx);
        if (ShouldActivateShadowSnapshotsBeforeDerived(ctx, resolvedPhases))
        {
            _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.PostScrapeEnrichment);
            _progress.SetSubOperation("activating_shadow_snapshots_early");
            _progress.BeginPhaseProgress(1);
            await RunPhaseAsync("ActivateShadowSnapshotsEarly", () =>
            {
                var activated = _persistence.FinalizeShadowSnapshots(ctx.ScrapeId, expectedPairs: expectedSnapshotPairs);
                _log.LogInformation(
                    "Activated shadow snapshot {ScrapeId} before derived readers ({Pairs} pair(s), {ExpectedPairs} expected).",
                    ctx.ScrapeId,
                    activated,
                    expectedSnapshotPairs.Count);
                _progress.ReportPhaseItemComplete();
                return Task.CompletedTask;
            });
        }

        // ── Band data collection (fire-and-forget background) ──
        // Skip if band data was already fetched via BandPageFetcher during the scrape pass.
        // BandScrape (new) uses the shared DOP pool inside ScrapeOrchestrator;
        // BandScrapePhase (legacy) is the old per-song sequential fetcher.
        Task? bandScrapeTask = null;
        bool bandAlreadyFetched = resolvedPhases.HasFlag(ScrapePhase.BandScrape);
        if (resolvedPhases.HasFlag(ScrapePhase.BandScrapePhase) && !bandAlreadyFetched)
        {
            var chartedSongs = service.Songs.Where(s => s.track?.su is not null).ToList();
            var bandAccessToken = await _tokenManager.GetAccessTokenAsync(ct);
            if (bandAccessToken is not null)
            {
                var bandCallerAccountId = _tokenManager.AccountId!;
                bandScrapeTask = Task.Run(
                    () => _bandScrapePhase.ExecuteAsync(chartedSongs, bandAccessToken, bandCallerAccountId, ct),
                    ct);
                _log.LogInformation("Band scrape launched in background ({Songs} songs).", chartedSongs.Count);
            }
            else
            {
                _log.LogWarning("No access token for band scrape. Will retry next pass.");
            }
        }

        // ── Band extraction (SQL-only) ──
        var bandExtractionResult = BandExtractionResult.Empty;
        if (resolvedPhases.HasFlag(ScrapePhase.BandExtraction))
        {
            _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.BandScraping);
            _progress.SetSubOperation("extracting_band_context");
            bandExtractionResult = await RunPhaseAsync("BandExtraction", () => _bandExtractor.RunAsync(ct), BandExtractionResult.Empty);
        }

        if (bandScrapeTask is not null)
        {
            try
            {
                await bandScrapeTask;
                _log.LogInformation("Background band scrape completed successfully.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Background band scrape failed. Band data may be incomplete this cycle.");
            }
            finally
            {
                bandScrapeTask = null;
            }
        }

        var registeredBandProcessingResult = RegisteredBandProcessingResult.Empty;
        if (ShouldRunRegisteredBandProcessing(resolvedPhases))
        {
            var registeredBandProcessingOrchestrator = _registeredBandProcessingOrchestrator!;
            var bandAccessToken = await _tokenManager.GetAccessTokenAsync(ct);
            if (bandAccessToken is not null)
            {
                var chartedSongIds = service.Songs
                    .Select(static song => song.track?.su)
                    .Where(static songId => !string.IsNullOrWhiteSpace(songId))
                    .Select(static songId => songId!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var seasonWindows = _persistence.Meta.GetSeasonWindows();
                _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.SongMachine);
                _progress.SetSubOperation("registered_band_targeted_processing");
                registeredBandProcessingResult = await RunPhaseAsync(
                    "RegisteredBandTargetedProcessing",
                    () => registeredBandProcessingOrchestrator.RunAsync(
                        chartedSongIds,
                        seasonWindows,
                        bandAccessToken,
                        _tokenManager.AccountId!,
                        _pool,
                        ct),
                    RegisteredBandProcessingResult.Empty);
            }
            else
            {
                _log.LogWarning("No access token for registered-band targeted processing. Will retry next pass.");
            }
        }

        if (ShouldRunBandMaintenance(resolvedPhases) || registeredBandProcessingResult.ImpactedTeamsByBandType.Count > 0)
        {
            _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.BandScraping);
            _progress.SetSubOperation("maintaining_band_projection");
            var mergedExtractionResult = bandExtractionResult with
            {
                ImpactedTeamsByBandType = MergeImpactedTeams(
                    bandExtractionResult.ImpactedTeamsByBandType,
                    registeredBandProcessingResult.ImpactedTeamsByBandType),
            };
            await RunPhaseAsync("BandMaintenance", () => RunBandMaintenanceAsync(ctx, mergedExtractionResult, ct));
        }

        // ── Solo rankings ──
        var rankingsSucceeded = false;
        if (resolvedPhases.HasFlag(ScrapePhase.SoloRankings))
            rankingsSucceeded = await RunPhaseAsync("ComputeRankings", () => ComputeRankingsAsync(service, ctx.ScrapeId, ct));

        // ── Solo rivals ──
        if (resolvedPhases.HasFlag(ScrapePhase.SoloRivals))
        {
            await RunPhaseAsync("Rivals", () => ComputeRivalsAsync(ctx, ct));
        }

        // ── Solo player stats + precompute ──
        if (resolvedPhases.HasFlag(ScrapePhase.SoloPlayerStats))
        {
            _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.Precomputing);
            await RunPhaseAsync("PlayerStatsTiers", () => ComputePlayerStatsTiersAsync(ctx, ct));
        }

        if (resolvedPhases.HasFlag(ScrapePhase.SoloPrecompute))
            await RunPhaseAsync("PrecomputeAll", () => _precomputer.PrecomputeAllAsync(ct));

        // ── Solo finalize ──
        if (resolvedPhases.HasFlag(ScrapePhase.SoloFinalize))
        {
            _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.Finalizing);
            _progress.RegisterBranches(new[] { "final_checkpoint", "pre_warming_cache" });
            await RunPhaseAsync("Checkpoint", () => Task.Run(() =>
            {
                _progress.StartBranch("final_checkpoint");
                _progress.SetSubOperation("final_checkpoint");
                try
                {
                    _persistence.CheckpointAll();
                    _progress.CompleteBranch("final_checkpoint", "complete");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _progress.CompleteBranch("final_checkpoint", "failed", ex.Message);
                    throw;
                }
            }, ct));

            StartBestEffortCacheWarm(ctx.RegisteredIds);

            if (ctx.ScrapeId > 0)
            {
                await RunPhaseAsync("ActivateShadowSnapshots", () =>
                {
                    _persistence.FinalizeShadowSnapshots(ctx.ScrapeId, wave: 2, expectedPairs: expectedSnapshotPairs);
                    return Task.CompletedTask;
                });
            }
        }

        // ── Await background band scrape for exception observation ──
        if (bandScrapeTask is not null)
        {
            try
            {
                await bandScrapeTask;
                _log.LogInformation("Background band scrape completed successfully.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Background band scrape failed. Band data may be incomplete this cycle.");
            }
        }
    }

    private static bool ShouldRunBandMaintenance(ScrapePhase resolvedPhases) =>
        resolvedPhases.HasFlag(ScrapePhase.BandScrape) ||
        resolvedPhases.HasFlag(ScrapePhase.BandScrapePhase) ||
        resolvedPhases.HasFlag(ScrapePhase.BandExtraction) ||
        resolvedPhases.HasFlag(ScrapePhase.SoloEnrichment);

    private bool ShouldRunRegisteredBandProcessing(ScrapePhase resolvedPhases) =>
        _registeredBandProcessingOrchestrator is not null &&
        _options.Value.EnableRegisteredBandTargetedProcessing &&
        (resolvedPhases.HasFlag(ScrapePhase.BandScrape) ||
         resolvedPhases.HasFlag(ScrapePhase.BandScrapePhase) ||
         resolvedPhases.HasFlag(ScrapePhase.BandExtraction) ||
         resolvedPhases.HasFlag(ScrapePhase.SoloRefreshUsers));

    private async Task RunBandMaintenanceAsync(ScrapePassContext ctx, BandExtractionResult extractionResult, CancellationToken ct)
    {
        var pruneResult = PruneBandEntries(ctx);
        var impactedTeams = MergeImpactedTeams(
            extractionResult.ImpactedTeamsByBandType,
            pruneResult.AffectedTeamsByBandType);

        if (_bandSearchProjectionBuilder is null)
            return;

        var refreshResult = await _bandSearchProjectionBuilder.RefreshIncrementalAsync(impactedTeams, ct);
        if (!refreshResult.ProjectionAvailable)
        {
            _log.LogDebug("Band search projection refresh skipped because no published projection state exists.");
            return;
        }

        _log.LogInformation(
            "Band search projection maintenance complete: {ImpactedTeams:N0} impacted team(s), " +
            "teams {DeletedTeams:N0}->{InsertedTeams:N0}, members {DeletedMembers:N0}->{InsertedMembers:N0}.",
            refreshResult.ImpactedTeams,
            refreshResult.DeletedTeamRows,
            refreshResult.InsertedTeamRows,
            refreshResult.DeletedMemberRows,
            refreshResult.InsertedMemberRows);
    }

    private static IReadOnlyDictionary<string, IReadOnlyCollection<string>> MergeImpactedTeams(
        params IReadOnlyDictionary<string, IReadOnlyCollection<string>>[] sources)
    {
        var merged = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            foreach (var (bandType, teamKeys) in source)
            {
                if (!merged.TryGetValue(bandType, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    merged[bandType] = set;
                }

                foreach (var teamKey in teamKeys)
                    set.Add(teamKey);
            }
        }

        return merged.ToDictionary(
            static kvp => kvp.Key,
            static kvp => (IReadOnlyCollection<string>)kvp.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private void StartBestEffortCacheWarm(IReadOnlyCollection<string> registeredIds)
    {
        _progress.StartBranch("pre_warming_cache");
        _progress.CompleteBranch("pre_warming_cache", "queued", "running after scrape completion");

        _ = Task.Run(() =>
        {
            try
            {
                _persistence.PreWarmRankingsCache(registeredIds);
                _log.LogInformation("Best-effort rankings cache warm completed for {UserCount:N0} registered user(s).", registeredIds.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Best-effort rankings cache warm failed. Cache entries will populate on demand.");
            }
        });
    }

    private static bool ShouldActivateShadowSnapshotsBeforeDerived(ScrapePassContext ctx, ScrapePhase resolvedPhases)
    {
        if (ctx.ScrapeId <= 0)
            return false;

        return resolvedPhases.HasFlag(ScrapePhase.SoloRankings)
            || resolvedPhases.HasFlag(ScrapePhase.SoloRivals)
            || resolvedPhases.HasFlag(ScrapePhase.SoloPlayerStats)
            || resolvedPhases.HasFlag(ScrapePhase.SoloPrecompute);
    }

    private static IReadOnlyList<(string SongId, string Instrument)> BuildExpectedSnapshotPairs(ScrapePassContext ctx)
    {
        var pairs = new HashSet<(string SongId, string Instrument)>();

        foreach (var request in ctx.ScrapeRequests)
        {
            if (string.IsNullOrWhiteSpace(request.SongId))
                continue;

            foreach (var instrument in request.Instruments)
            {
                if (string.IsNullOrWhiteSpace(instrument) || ScrapeOrchestrator.IsBandInstrument(instrument))
                    continue;

                pairs.Add((request.SongId, instrument));
            }
        }

        return pairs.ToArray();
    }

    /// <summary>
    /// Run a post-scrape phase with timing and heap telemetry.
    /// Logs phase name, duration, and heap delta so the peak memory owner is identifiable.
    /// </summary>
    private async Task RunPhaseAsync(string phaseName, Func<Task> phase)
    {
        var heapBefore = GC.GetTotalMemory(false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await phase();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PostScrape phase [{Phase}] failed. Will retry next pass.", phaseName);
        }
        sw.Stop();
        var heapAfter = GC.GetTotalMemory(false);
        _log.LogInformation(
            "PostScrape phase [{Phase}] completed in {Elapsed}. Heap: {Before:N0} → {After:N0} ({Delta:+#,0;-#,0;0} bytes).",
            phaseName, sw.Elapsed, heapBefore, heapAfter, heapAfter - heapBefore);
    }

    /// <summary>
    /// Run a post-scrape phase that returns a result, with timing and heap telemetry.
    /// </summary>
    private async Task<T> RunPhaseAsync<T>(string phaseName, Func<Task<T>> phase, T defaultValue = default!)
    {
        var heapBefore = GC.GetTotalMemory(false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        T result = defaultValue;
        try
        {
            result = await phase();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PostScrape phase [{Phase}] failed. Will retry next pass.", phaseName);
        }
        sw.Stop();
        var heapAfter = GC.GetTotalMemory(false);
        _log.LogInformation(
            "PostScrape phase [{Phase}] completed in {Elapsed}. Heap: {Before:N0} → {After:N0} ({Delta:+#,0;-#,0;0} bytes).",
            phaseName, sw.Elapsed, heapBefore, heapAfter, heapAfter - heapBefore);
        return result;
    }

    /// <summary>
    /// Four operations with partial parallelism: rank recomputation runs first,
    /// then pruning starts in parallel with FirstSeenSeason and account name resolution.
    /// Pruning only needs CHOpt max scores and registered IDs — it does not depend on
    /// FirstSeenSeason or account names.
    /// </summary>
    internal async Task RunEnrichmentAsync(ScrapePassContext ctx, FestivalService service, CancellationToken ct)
    {
        _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.PostScrapeEnrichment);
        _progress.RegisterBranches(new[] { "rank_recompute", "first_seen", "name_resolution", "pruning" });
        _progress.SetSubOperation("enriching_parallel_rank_recompute");

        var rankTask = Task.Run(() =>
        {
            _progress.StartBranch("rank_recompute");
            try
            {
                var rankChangedSongs = ctx.Aggregates?.RankChangedSongIds;
                if (rankChangedSongs is { Count: > 0 })
                {
                    _progress.SetBranchTotal("rank_recompute", rankChangedSongs.Count);
                    _log.LogInformation("Recomputing ranks for {Count:N0} changed song(s) (of {Total:N0} total).",
                        rankChangedSongs.Count, ctx.ScrapeRequests.Count);
                    var rankUpdated = _persistence.RecomputeRanksForSongs(rankChangedSongs);
                    _progress.ReportBranchProgress("rank_recompute", rankChangedSongs.Count);
                    _log.LogInformation("Recomputed ranks across all instruments: {Count:N0} entries updated.", rankUpdated);
                    _progress.CompleteBranch("rank_recompute", "complete", $"{rankUpdated:N0} entries updated");
                }
                else
                {
                    _log.LogInformation("No songs with rank-affecting changes. Skipping rank recomputation.");
                    _progress.CompleteBranch("rank_recompute", "skipped", "no rank-affecting changes");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Rank recomputation failed. Stored ranks may be stale.");
                _progress.CompleteBranch("rank_recompute", "failed", ex.Message);
            }
        }, ct);

        var firstSeenTask = Task.Run(async () =>
        {
            _progress.StartBranch("first_seen");
            try
            {
                var firstSeenToken = await _tokenManager.GetAccessTokenAsync(ct);
                if (firstSeenToken is not null)
                {
                    var firstSeenCount = await _firstSeenCalculator.CalculateAsync(
                        service, firstSeenToken, _tokenManager.AccountId!,
                        _pool, ct);
                    if (firstSeenCount > 0)
                        _log.LogInformation("Calculated FirstSeenSeason for {Count} song(s).", firstSeenCount);
                    _progress.CompleteBranch("first_seen", "complete",
                        firstSeenCount > 0 ? $"{firstSeenCount:N0} song(s) calculated" : "no songs needed calculation");
                }
                else
                {
                    _log.LogWarning("No access token for FirstSeenSeason calculation. Will retry next pass.");
                    _progress.CompleteBranch("first_seen", "skipped", "no access token");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "FirstSeenSeason calculation failed. Will retry next pass.");
                _progress.CompleteBranch("first_seen", "failed", ex.Message);
            }
        }, ct);

        var nameResTask = Task.Run(async () =>
        {
            _progress.StartBranch("name_resolution");
            try
            {
                await _nameResolver.ResolveNewAccountsAsync(maxConcurrency: _options.Value.PageConcurrency, ct);
                _progress.CompleteBranch("name_resolution", "complete");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Account name resolution failed. Will retry next pass.");
                _progress.CompleteBranch("name_resolution", "failed", ex.Message);
            }
        }, ct);

        // Wait for ranks first — pruning needs fresh ranks in the DB, but does not
        // need firstSeen or account names.  Fire pruning in parallel with the tail.
        await rankTask;
        _progress.SetSubOperation("enriching_parallel_tail");

        var pruneTask = Task.Run(() =>
        {
            _progress.SetSubOperation("pruning_excess_entries");
            _progress.StartBranch("pruning");
            try
            {
                PruneExcessEntries(ctx);
                _progress.CompleteBranch("pruning", "complete");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _progress.CompleteBranch("pruning", "failed", ex.Message);
            }
        }, ct);

        await Task.WhenAll(firstSeenTask, nameResTask, pruneTask);
    }

    /// <summary>
    /// Run account name resolution standalone (for --resolve-only mode).
    /// </summary>
    public Task<int> ResolveNamesAsync(int maxConcurrency, CancellationToken ct)
        => _nameResolver.ResolveNewAccountsAsync(maxConcurrency, ct);

    /// <summary>
    /// Compute per-instrument + composite + combo rankings and daily history snapshots.
    /// Runs after enrichment/pruning and registered-user refresh, before rivals.
    /// </summary>
    internal Task<bool> ComputeRankingsAsync(FestivalService service, CancellationToken ct)
        => ComputeRankingsAsync(service, 0, ct);

    internal async Task<bool> ComputeRankingsAsync(FestivalService service, long scrapeId, CancellationToken ct)
    {
        try
        {
            _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.ComputingRankings);
            await _rankingsCalculator.ComputeAllAsync(service, ct, scrapeId);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Rankings computation failed. Will retry next pass.");
            return false;
        }
    }

    /// <summary>
    /// Refresh stale/missing entries for registered users using the song processing machine.
    /// Also processes pending backfill and history recon users in the same run.
    /// All songs are processed in parallel, bounded by the shared DOP pool.
    /// </summary>
    internal async Task RefreshRegisteredUsersAsync(ScrapePassContext ctx, CancellationToken ct)
    {
        _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.SongMachine);

        try
        {
            var refreshToken = await _tokenManager.GetAccessTokenAsync(ct);
            if (refreshToken is null)
            {
                _log.LogWarning("No access token for post-scrape refresh. Will retry next pass.");
                return;
            }

            var callerAccountId = _tokenManager.AccountId!;

            // Discover season windows. Runs every pass regardless of registered-user
            // count so the current-season signal (consumed by /api/songs via
            // MetaDatabase.GetCurrentSeason) stays fresh across season rollovers.
            _progress.SetSubOperation("discovering_season_windows");
            IReadOnlyList<Persistence.SeasonWindowInfo> seasonWindows;
            try
            {
                seasonWindows = await _historyReconstructor.DiscoverSeasonWindowsAsync(
                    refreshToken, callerAccountId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Season window discovery failed. Using empty season list.");
                seasonWindows = [];
            }

            // Backstop: if the scraper has observed higher-numbered seasons in the
            // instrument DBs than the events API advertised (e.g. Epic renamed a
            // window and our regex missed the current season), persist a window
            // row for that season so GetCurrentSeason() reflects reality. event_id
            // and window_id are left blank; real values will be filled in when the
            // next events-API response matches.
            var instrumentMaxSeason = _persistence.GetMaxSeasonAcrossInstruments();
            if (instrumentMaxSeason is int floor)
            {
                var known = seasonWindows.Select(w => w.SeasonNumber).ToHashSet();
                for (int s = 1; s <= floor; s++)
                {
                    if (known.Contains(s)) continue;
                    _persistence.Meta.UpsertSeasonWindow(s, eventId: "", windowId: "");
                }
                if (floor > (seasonWindows.Count == 0 ? 0 : seasonWindows.Max(w => w.SeasonNumber)))
                {
                    _log.LogInformation(
                        "Season window floor raised from events-API max to instrument-DB max (season {Season}).",
                        floor);
                    seasonWindows = _persistence.Meta.GetSeasonWindows();
                }
            }

            if (ctx.RegisteredIds.Count == 0)
                return;

            var chartedSongIds = ctx.ScrapeRequests.Select(r => r.SongId).ToList();
            var currentSeason = instrumentMaxSeason ?? 1;
            var allSeasons = seasonWindows.Select(w => w.SeasonNumber).ToHashSet();

            // ── Build user list ──────────────────────────────────
            var users = new List<UserWorkItem>();

            // Post-scrape users
            foreach (var accountId in ctx.RegisteredIds)
            {
                var seasonsNeeded = new HashSet<int>();
                if (_options.Value.RefreshCurrentSeasonSessions)
                    seasonsNeeded.Add(currentSeason);

                users.Add(new UserWorkItem
                {
                    AccountId = accountId,
                    Purposes = WorkPurpose.PostScrape,
                    AllTimeNeeded = true,
                    SeasonsNeeded = seasonsNeeded,
                });
            }

            // Pending backfill users
            var pendingBackfills = _persistence.Meta.GetPendingBackfills();
            foreach (var bf in pendingBackfills)
            {
                var alreadyChecked = _persistence.Meta.GetCheckedBackfillPairs(bf.AccountId);
                users.Add(new UserWorkItem
                {
                    AccountId = bf.AccountId,
                    Purposes = WorkPurpose.Backfill | WorkPurpose.HistoryRecon,
                    AllTimeNeeded = true,
                    SeasonsNeeded = new HashSet<int>(allSeasons),
                    AlreadyChecked = alreadyChecked,
                });
            }

            // Pending history recon users
            foreach (var accountId in ctx.RegisteredIds)
            {
                var backfillStatus = _persistence.Meta.GetBackfillStatus(accountId);
                if (backfillStatus?.Status != "complete") continue;

                var reconStatus = _persistence.Meta.GetHistoryReconStatus(accountId);
                if (reconStatus?.Status == "complete") continue;

                if (pendingBackfills.Any(b => b.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var alreadyProcessed = _persistence.Meta.GetProcessedHistoryReconPairs(accountId);
                users.Add(new UserWorkItem
                {
                    AccountId = accountId,
                    Purposes = WorkPurpose.HistoryRecon,
                    AllTimeNeeded = false,
                    SeasonsNeeded = new HashSet<int>(allSeasons),
                    AlreadyChecked = alreadyProcessed,
                });
            }

            // ── Attach to the cyclical machine ──────────────────
            _progress.SetSubOperation("processing_songs");
            var result = await _cyclicalMachine.AttachAsync(
                users, chartedSongIds, seasonWindows,
                SongMachineSource.PostScrape,
                isHighPriority: true,
                ct: ct,
                preserveProgressPhaseOnIdle: true);

            if (result.EntriesUpdated > 0 || result.SessionsInserted > 0)
                _log.LogInformation("Song machine updated {Entries} entries, {Sessions} sessions for {Users} users.",
                    result.EntriesUpdated, result.SessionsInserted, result.UsersProcessed);

            // ── Handle per-user completion inline ────────────────
            _progress.SetSubOperation("completing_user_actions");
            foreach (var user in users.Where(u => u.Purposes.HasFlag(WorkPurpose.Backfill)))
            {
                try
                {
                    _persistence.Meta.CompleteBackfill(user.AccountId);
                    _rivalsOrchestrator.ComputeForUser(user.AccountId);
                    _ = _notifications.NotifyBackfillCompleteAsync(user.AccountId);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Post-backfill actions failed for {AccountId}.", user.AccountId);
                }
            }

            foreach (var user in users.Where(u => u.Purposes.HasFlag(WorkPurpose.HistoryRecon)))
            {
                try
                {
                    var reconStatus = _persistence.Meta.GetHistoryReconStatus(user.AccountId);
                    if (reconStatus?.Status == "complete") continue;

                    if (reconStatus is null)
                        _persistence.Meta.EnqueueHistoryRecon(user.AccountId, 0);

                    _persistence.Meta.CompleteHistoryRecon(user.AccountId);
                    _ = _notifications.NotifyHistoryReconCompleteAsync(user.AccountId);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Post-history-recon actions failed for {AccountId}.", user.AccountId);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Song processing machine failed. Will retry next pass.");
        }
    }

    /// <summary>
    /// Compute rivals for registered users whose scores (or rivals' scores) changed.
    /// </summary>
    internal async Task ComputeRivalsAsync(ScrapePassContext ctx, CancellationToken ct)
    {
        if (ctx.RegisteredIds.Count == 0)
            return;

        try
        {
            var dirtySongs = ctx.Aggregates.DirtyRivalSongs
                .Where(row => ctx.RegisteredIds.Contains(row.AccountId))
                .ToList();

            _log.LogInformation(
                "Song-rivals dirty summary: dirtySongs={DirtySongs}, dirtyAccounts={DirtyAccounts}, reasons={DirtyReasonCounts}.",
                dirtySongs.Count,
                dirtySongs.Select(row => row.AccountId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                FormatCountSummary(dirtySongs.GroupBy(row => row.DirtyReason, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase)));

            if (dirtySongs.Count > 0)
                _persistence.Meta.UpsertDirtyRivalSongs(dirtySongs);

            await _rivalsOrchestrator.ComputeAllAsync(ctx.RegisteredIds, null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Rivals computation failed. Will retry next pass.");
        }
    }

    private static string FormatCountSummary(IReadOnlyDictionary<string, int> counts)
    {
        if (counts.Count == 0)
            return "none";

        return string.Join(", ",
            counts.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}={pair.Value}"));
    }

    /// <summary>
    /// Compute leaderboard rivals for registered users. Per instrument per rank method,
    /// finds neighbors and compares shared songs.
    /// </summary>
    internal async Task ComputeLeaderboardRivalsAsync(ScrapePassContext ctx, CancellationToken ct)
    {
        if (ctx.RegisteredIds.Count == 0)
            return;

        try
        {
            _log.LogInformation("Computing leaderboard rivals for {Count} registered user(s).", ctx.RegisteredIds.Count);

            var tasks = ctx.RegisteredIds.Select(accountId => Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var result = _leaderboardRivalsCalculator.ComputeForUser(accountId);
                    _log.LogDebug(
                        "Computed leaderboard rivals for {AccountId}: {Rivals} rival rows, {Samples} sample rows.",
                        accountId, result.RivalCount, result.SampleCount);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Leaderboard rivals computation failed for {AccountId}.", accountId);
                }
            }, ct)).ToList();

            await Task.WhenAll(tasks);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Leaderboard rivals computation failed. Will retry next pass.");
        }
    }

    /// <summary>
    /// Prune excess entries from instrument DBs down to the configured max per song,
    /// preserving registered users. When CHOpt max scores are available, entries above
    /// the over-threshold boundary are exempt from pruning so that deep-scraped valid
    /// entries are not discarded along with exploited scores.
    /// Only depends on CHOpt max scores and registered IDs — runs in parallel with
    /// FirstSeenSeason and account name resolution during enrichment.
    /// </summary>
    internal void PruneExcessEntries(ScrapePassContext ctx)
    {
        var maxPages = _options.Value.MaxPagesPerLeaderboard;
        if (maxPages <= 0) return; // unlimited — no pruning

        if (!_persistence.WriteLegacyLiveLeaderboardDuringScrape)
        {
            _log.LogInformation(
                "Skipping legacy live leaderboard excess prune because legacy live scrape writes are disabled; snapshot current-state replaces foreground solo prune.");
            return;
        }

        var maxEntries = maxPages * 100;
        try
        {
            // Build per-instrument, per-song threshold maps from CHOpt max scores.
            // Entries above CHOpt max × cutoff multiplier are kept unconditionally;
            // the maxEntries cap applies only to entries at or below the cutoff.
            var allMaxScores = _pathDataStore.GetAllMaxScores();
            var cutoffMultiplier = _options.Value.ValidCutoffMultiplier;
            Dictionary<string, IReadOnlyDictionary<string, int>>? thresholds = null;

            if (allMaxScores.Count > 0)
            {
                thresholds = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

                foreach (var instrument in _persistence.GetInstrumentKeys())
                {
                    var songMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (songId, maxScores) in allMaxScores)
                    {
                        var choptMax = maxScores.GetByInstrument(instrument);
                        if (choptMax.HasValue)
                            songMap[songId] = (int)(choptMax.Value * cutoffMultiplier);
                    }
                    if (songMap.Count > 0)
                        thresholds[instrument] = songMap;
                }

                if (thresholds.Count == 0)
                    thresholds = null;
            }

            var deleted = _persistence.PruneAllInstruments(maxEntries, ctx.RegisteredIds, thresholds);
            if (deleted > 0)
                _log.LogInformation("Pruned {Deleted:N0} excess entries (keeping top {Max:N0} valid per song + registered users).",
                    deleted, maxEntries);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Entry pruning failed. Will retry next pass.");
        }
    }

    /// <summary>
    /// Prune excess band entries. For each song × band type, keep all over-threshold
    /// entries at the top plus the next 10K valid entries plus any team containing a
    /// registered user. Cascades to band_member_stats and band_members.
    /// </summary>
    internal BandPruneResult PruneBandEntries(ScrapePassContext ctx)
    {
        try
        {
            var result = _bandPersistence.PruneBandEntriesDetailed(ctx.RegisteredIds);
            if (result.DeletedEntries > 0)
                _log.LogInformation("Band pruning complete: {Deleted:N0} entries removed.", result.DeletedEntries);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Band entry pruning failed. Will retry next pass.");
            return BandPruneResult.Empty;
        }
    }

    /// <summary>
    /// Compute leeway-tiered player stats for accounts whose scores changed in this scrape.
    /// Pass 2 of the two-pass incremental strategy — score-dependent aggregates only.
    /// (Pass 1 — rank refresh for all accounts — is future work.)
    /// </summary>
    internal async Task ComputePlayerStatsTiersAsync(ScrapePassContext ctx, CancellationToken ct)
    {
        var changedIds = ctx.Aggregates.ChangedAccountIds;
        // Also include registered users (their stats should always be fresh)
        var accountIds = new HashSet<string>(changedIds, StringComparer.OrdinalIgnoreCase);
        foreach (var id in ctx.RegisteredIds)
            accountIds.Add(id);

        if (accountIds.Count == 0) return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _log.LogInformation("Computing player stats tiers for {Count:N0} accounts ({Changed:N0} changed + {Registered:N0} registered).",
            accountIds.Count, changedIds.Count, ctx.RegisteredIds.Count);

        var allMaxScores = _pathDataStore.GetAllMaxScores();
        var metaDb = _persistence.Meta;
        var instrumentKeys = _persistence.GetInstrumentKeys();
        int totalSongs = _persistence.GetTotalSongCount();
        var population = metaDb.GetAllLeaderboardPopulation();
        int computed = 0;

        await Parallel.ForEachAsync(accountIds,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            (accountId, innerCt) =>
            {
                try
                {
                    ComputeAndStorePlayerStats(accountId, allMaxScores, instrumentKeys, totalSongs, population, metaDb);
                    Interlocked.Increment(ref computed);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Stats tier computation failed for {AccountId}.", accountId);
                }
                return ValueTask.CompletedTask;
            });

        sw.Stop();
        _log.LogInformation("Computed player stats tiers for {Computed:N0}/{Total:N0} accounts in {Elapsed:F1}s.",
            computed, accountIds.Count, sw.Elapsed.TotalSeconds);
    }

    private void ComputeAndStorePlayerStats(
        string accountId,
        Dictionary<string, SongMaxScores> allMaxScores,
        IReadOnlyList<string> instrumentKeys,
        int totalSongs,
        Dictionary<(string SongId, string Instrument), long> population,
        IMetaDatabase metaDb)
    {
        var allScores = _persistence.GetCurrentStatePlayerProfile(accountId);
        if (allScores.Count == 0) return;

        // Group scores by instrument
        var byInstrument = new Dictionary<string, List<PlayerScoreDto>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in allScores)
        {
            if (!byInstrument.TryGetValue(s.Instrument, out var list))
            {
                list = new List<PlayerScoreDto>();
                byInstrument[s.Instrument] = list;
            }
            list.Add(s);
        }

        // Get fallbacks for registered users (score_history)
        // For non-registered accounts, fallbacks will be null → scores are excluded instead
        Dictionary<(string SongId, string Instrument), List<ValidScoreFallback>>? fallbacks = null;
        var maxThresholds = new Dictionary<(string SongId, string Instrument), int>();
        foreach (var s in allScores)
        {
            if (!allMaxScores.TryGetValue(s.SongId, out var ms)) continue;
            var max = ms.GetByInstrument(s.Instrument);
            if (max.HasValue && max.Value > 0 && s.Score > max.Value)
                maxThresholds[(s.SongId, s.Instrument)] = (int)(max.Value * 1.05);
        }
        if (maxThresholds.Count > 0)
            fallbacks = metaDb.GetAllValidScoreTiers(accountId, maxThresholds);

        var rows = new List<PlayerStatsTiersRow>();
        var perInstrumentTiers = new Dictionary<string, List<PlayerStatsTier>>(StringComparer.OrdinalIgnoreCase);

        foreach (var inst in instrumentKeys)
        {
            var scores = byInstrument.GetValueOrDefault(inst);
            if (scores is null || scores.Count == 0) continue;

            var tiers = PlayerStatsCalculator.ComputeTiers(scores, allMaxScores, inst, totalSongs, population, fallbacks);
            perInstrumentTiers[inst] = tiers;

            rows.Add(new PlayerStatsTiersRow
            {
                AccountId = accountId,
                Instrument = inst,
                TiersJson = System.Text.Json.JsonSerializer.Serialize(tiers),
            });
        }

        // Compute "Overall" tier
        if (perInstrumentTiers.Count > 0)
        {
            var overallTiers = PlayerStatsCalculator.ComputeOverallTiers(perInstrumentTiers, totalSongs);
            rows.Add(new PlayerStatsTiersRow
            {
                AccountId = accountId,
                Instrument = "Overall",
                TiersJson = System.Text.Json.JsonSerializer.Serialize(overallTiers),
            });
        }

        metaDb.UpsertPlayerStatsTiersBatch(rows);
    }
}
