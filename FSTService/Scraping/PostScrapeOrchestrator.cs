using FortniteFestival.Core.Scraping;
using FortniteFestival.Core.Services;
using FSTService.Api;
using FSTService.Auth;
using FSTService.Persistence;
using Microsoft.Extensions.Options;

namespace FSTService.Scraping;

/// <summary>
/// Orchestrates the post-scrape enrichment phases: parallel rank/firstSeen/nameRes,
/// personal DB rebuild, refresh of registered users, and session cleanup.
/// Extracted from <see cref="ScraperWorker"/> to reduce its dependency count and
/// make each phase independently testable.
/// </summary>
public sealed class PostScrapeOrchestrator
{
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly FirstSeenSeasonCalculator _firstSeenCalculator;
    private readonly AccountNameResolver _nameResolver;
    private readonly PersonalDbBuilder _personalDbBuilder;
    private readonly PostScrapeRefresher _refresher;
    private readonly SongProcessingMachine _machine;
    private readonly HistoryReconstructor _historyReconstructor;
    private readonly RivalsOrchestrator _rivalsOrchestrator;
    private readonly RankingsCalculator _rankingsCalculator;
    private readonly NotificationService _notifications;
    private readonly TokenManager _tokenManager;
    private readonly ScrapeProgressTracker _progress;
    private readonly IOptions<ScraperOptions> _options;
    private readonly ILogger<PostScrapeOrchestrator> _log;

    public PostScrapeOrchestrator(
        GlobalLeaderboardPersistence persistence,
        FirstSeenSeasonCalculator firstSeenCalculator,
        AccountNameResolver nameResolver,
        PersonalDbBuilder personalDbBuilder,
        PostScrapeRefresher refresher,
        SongProcessingMachine machine,
        HistoryReconstructor historyReconstructor,
        RivalsOrchestrator rivalsOrchestrator,
        RankingsCalculator rankingsCalculator,
        NotificationService notifications,
        TokenManager tokenManager,
        ScrapeProgressTracker progress,
        IOptions<ScraperOptions> options,
        ILogger<PostScrapeOrchestrator> log)
    {
        _persistence = persistence;
        _firstSeenCalculator = firstSeenCalculator;
        _nameResolver = nameResolver;
        _personalDbBuilder = personalDbBuilder;
        _refresher = refresher;
        _machine = machine;
        _historyReconstructor = historyReconstructor;
        _rivalsOrchestrator = rivalsOrchestrator;
        _rankingsCalculator = rankingsCalculator;
        _notifications = notifications;
        _tokenManager = tokenManager;
        _progress = progress;
        _options = options;
        _log = log;
    }

    /// <summary>
    /// Run all post-scrape phases in sequence: enrichment (parallel), personal DB
    /// rebuild, registered user refresh, and session cleanup.
    /// </summary>
    public async Task RunAsync(ScrapePassContext ctx, FestivalService service, CancellationToken ct)
    {
        await RunEnrichmentAsync(ctx, service, ct);
        PruneExcessEntries(ctx);

        // Refresh registered users BEFORE rankings so that low scores (below the
        // global-scrape cutoff) are present in the instrument DBs when rankings
        // are computed.  Without this, newly-refreshed entries would only appear
        // in the next scrape pass's ranking calculation.
        var refreshConcurrency = Math.Max(1, _options.Value.SongConcurrency)
            * GlobalLeaderboardScraper.AllInstruments.Count
            * Math.Max(1, _options.Value.PageConcurrency);
        int initialDop = Math.Max(1, refreshConcurrency / 2);
        using var limiter = new AdaptiveConcurrencyLimiter(initialDop, minDop: 2, maxDop: refreshConcurrency, _log);

        await RefreshRegisteredUsersAsync(ctx, limiter, ct);
        await ComputeRankingsAsync(service, ct);
        await RebuildPersonalDbsAsync(ctx, ct);
        await ComputeRivalsAsync(ctx, ct);
        CleanupSessions();

        // Checkpoint all WAL files after post-scrape writes (enrichment, refresh,
        // rankings) to keep them small for subsequent API reads.
        _persistence.CheckpointAll();
    }

    /// <summary>
    /// Three independent operations run in parallel: rank recomputation,
    /// FirstSeenSeason calculation, and account name resolution.
    /// </summary>
    internal async Task RunEnrichmentAsync(ScrapePassContext ctx, FestivalService service, CancellationToken ct)
    {
        _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.PostScrapeEnrichment);

        var rankTask = Task.Run(() =>
        {
            try
            {
                var rankUpdated = _persistence.RecomputeAllRanks();
                _log.LogInformation("Recomputed ranks across all instruments: {Count:N0} entries updated.", rankUpdated);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Rank recomputation failed. Stored ranks may be stale.");
            }
        }, ct);

        var firstSeenTask = Task.Run(async () =>
        {
            try
            {
                var firstSeenToken = await _tokenManager.GetAccessTokenAsync(ct);
                if (firstSeenToken is not null)
                {
                    var firstSeenCount = await _firstSeenCalculator.CalculateAsync(
                        service, firstSeenToken, _tokenManager.AccountId!,
                        _options.Value.PageConcurrency, ct);
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
        }, ct);

        var nameResTask = Task.Run(async () =>
        {
            try
            {
                await _nameResolver.ResolveNewAccountsAsync(maxConcurrency: _options.Value.PageConcurrency, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Account name resolution failed. Will retry next pass.");
            }
        }, ct);

        await Task.WhenAll(rankTask, firstSeenTask, nameResTask);
    }

    /// <summary>
    /// Run account name resolution standalone (for --resolve-only mode).
    /// </summary>
    public Task<int> ResolveNamesAsync(int maxConcurrency, CancellationToken ct)
        => _nameResolver.ResolveNewAccountsAsync(maxConcurrency, ct);

    /// <summary>
    /// Compute per-instrument + composite + combo rankings and daily history snapshots.
    /// Runs after enrichment/pruning and registered-user refresh, before personal DB rebuild and rivals.
    /// </summary>
    internal async Task ComputeRankingsAsync(FestivalService service, CancellationToken ct)
    {
        try
        {
            _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.ComputingRankings);
            await _rankingsCalculator.ComputeAllAsync(service, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Rankings computation failed. Will retry next pass.");
        }
    }

    /// <summary>
    /// Rebuild personal DBs for registered users whose scores changed during the scrape.
    /// </summary>
    internal async Task RebuildPersonalDbsAsync(ScrapePassContext ctx, CancellationToken ct)
    {
        if (ctx.Aggregates.ChangedAccountIds.Count == 0)
            return;

        _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.RebuildingPersonalDbs);
        _progress.BeginPhaseProgress(totalItems: 0, totalAccounts: ctx.Aggregates.ChangedAccountIds.Count);
        try
        {
            var changedIds = new HashSet<string>(ctx.Aggregates.ChangedAccountIds, StringComparer.OrdinalIgnoreCase);
            var rebuilt = _personalDbBuilder.RebuildForAccounts(changedIds, _persistence.Meta);
            if (rebuilt > 0)
            {
                _log.LogInformation("Rebuilt {Count} personal DB(s) for users with score changes.", rebuilt);

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

    /// <summary>
    /// Refresh stale/missing entries for registered users using the song processing machine.
    /// Also enqueues backfill and history recon users into the same machine run.
    /// </summary>
    internal async Task RefreshRegisteredUsersAsync(ScrapePassContext ctx, AdaptiveConcurrencyLimiter limiter, CancellationToken ct)
    {
        if (ctx.RegisteredIds.Count == 0)
            return;

        _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.SongMachine);

        try
        {
            var seenSet = new HashSet<(string AccountId, string SongId, string Instrument)>(
                ctx.Aggregates.SeenRegisteredEntries);
            var chartedSongIds = ctx.ScrapeRequests.Select(r => r.SongId).ToList();

            var refreshToken = await _tokenManager.GetAccessTokenAsync(ct);
            if (refreshToken is null)
            {
                _log.LogWarning("No access token for post-scrape refresh. Will retry next pass.");
                return;
            }

            var callerAccountId = _tokenManager.AccountId!;

            // Discover season windows for history recon
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

            var currentSeason = _persistence.GetMaxSeasonAcrossInstruments() ?? 1;

            // ── Enqueue post-scrape users ────────────────────────
            foreach (var accountId in ctx.RegisteredIds)
            {
                // Skip users whose entries were already seen in the global scrape
                // for every song/instrument — they don't need any refresh.
                var seasonsNeeded = new HashSet<int>();
                if (_options.Value.RefreshCurrentSeasonSessions)
                    seasonsNeeded.Add(currentSeason);

                _machine.EnqueueUser(new UserWorkItem
                {
                    AccountId = accountId,
                    Purposes = WorkPurpose.PostScrape,
                    AllTimeNeeded = true,
                    SeasonsNeeded = seasonsNeeded,
                });
            }

            // ── Also enqueue pending backfill users ──────────────
            var pendingBackfills = _persistence.Meta.GetPendingBackfills();
            var allSeasons = seasonWindows.Select(w => w.SeasonNumber).ToHashSet();
            foreach (var bf in pendingBackfills)
            {
                var alreadyChecked = _persistence.Meta.GetCheckedBackfillPairs(bf.AccountId);
                _machine.EnqueueUser(new UserWorkItem
                {
                    AccountId = bf.AccountId,
                    Purposes = WorkPurpose.Backfill | WorkPurpose.HistoryRecon,
                    AllTimeNeeded = true,
                    SeasonsNeeded = new HashSet<int>(allSeasons),
                    AlreadyChecked = alreadyChecked,
                });
            }

            // ── Also enqueue pending history recon users ─────────
            foreach (var accountId in ctx.RegisteredIds)
            {
                var backfillStatus = _persistence.Meta.GetBackfillStatus(accountId);
                if (backfillStatus?.Status != "complete") continue;

                var reconStatus = _persistence.Meta.GetHistoryReconStatus(accountId);
                if (reconStatus?.Status == "complete") continue;

                // Don't double-enqueue if already in pending backfills
                if (pendingBackfills.Any(b => b.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var alreadyProcessed = _persistence.Meta.GetProcessedHistoryReconPairs(accountId);
                _machine.EnqueueUser(new UserWorkItem
                {
                    AccountId = accountId,
                    Purposes = WorkPurpose.HistoryRecon,
                    AllTimeNeeded = false,
                    SeasonsNeeded = new HashSet<int>(allSeasons),
                    AlreadyChecked = alreadyProcessed,
                });
            }

            // ── Run the machine ──────────────────────────────────
            var completionHandler = new PostScrapeCompletionHandler(
                _persistence, _personalDbBuilder, _rivalsOrchestrator, _notifications, _log);

            var result = await _machine.RunAsync(
                chartedSongIds, seasonWindows,
                refreshToken, callerAccountId,
                limiter, _options.Value.LookupBatchSize,
                completionHandler, ct);

            if (result.EntriesUpdated > 0)
                _log.LogInformation("Song machine updated {Entries} entries, {Sessions} sessions for {Users} users.",
                    result.EntriesUpdated, result.SessionsInserted, result.UsersProcessed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Song processing machine failed. Will retry next pass.");
        }
    }

    /// <summary>
    /// Handles completion events from the song processing machine during post-scrape.
    /// Triggers personal DB rebuilds, rivals computation, and notifications.
    /// </summary>
    private sealed class PostScrapeCompletionHandler : IWorkCompletionHandler
    {
        private readonly GlobalLeaderboardPersistence _persistence;
        private readonly PersonalDbBuilder _personalDbBuilder;
        private readonly RivalsOrchestrator _rivalsOrchestrator;
        private readonly NotificationService _notifications;
        private readonly ILogger _log;

        public PostScrapeCompletionHandler(
            GlobalLeaderboardPersistence persistence,
            PersonalDbBuilder personalDbBuilder,
            RivalsOrchestrator rivalsOrchestrator,
            NotificationService notifications,
            ILogger log)
        {
            _persistence = persistence;
            _personalDbBuilder = personalDbBuilder;
            _rivalsOrchestrator = rivalsOrchestrator;
            _notifications = notifications;
            _log = log;
        }

        public void OnPostScrapeComplete(IReadOnlySet<string> accountIds)
        {
            _log.LogInformation("Post-scrape refresh complete for {Count} registered users.", accountIds.Count);
        }

        public void OnUserBackfillComplete(string accountId)
        {
            _persistence.Meta.CompleteBackfill(accountId);
            _log.LogInformation("Backfill complete for {AccountId} (via song machine).", accountId);

            try
            {
                _rivalsOrchestrator.ComputeForUser(accountId);
                _personalDbBuilder.RebuildForAccounts(
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { accountId },
                    _persistence.Meta);
                // Fire-and-forget notifications (best effort)
                _ = _notifications.NotifyBackfillCompleteAsync(accountId);
                _ = _notifications.NotifyPersonalDbReadyAsync(accountId);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Post-backfill actions failed for {AccountId}.", accountId);
            }
        }

        public void OnMachineIdle()
        {
            _log.LogDebug("Song processing machine idle.");
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
            // Build dirty-instruments map from ChangedAccountIds.
            // For v1, any change on a user triggers full recompute (no per-instrument tracking yet).
            Dictionary<string, HashSet<string>>? dirtyMap = null;
            if (ctx.Aggregates.ChangedAccountIds.Count > 0)
            {
                dirtyMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                // For now, mark all instruments dirty for any changed registered user
                foreach (var accountId in ctx.Aggregates.ChangedAccountIds)
                {
                    if (ctx.RegisteredIds.Contains(accountId))
                        dirtyMap[accountId] = null!; // null = all instruments
                }
            }

            await _rivalsOrchestrator.ComputeAllAsync(ctx.RegisteredIds, dirtyMap, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Rivals computation failed. Will retry next pass.");
        }
    }

    /// <summary>
    /// Prune excess entries from instrument DBs down to the configured max per song,
    /// preserving registered users. Runs after rank recomputation so ranks are fresh.
    /// </summary>
    internal void PruneExcessEntries(ScrapePassContext ctx)
    {
        var maxPages = _options.Value.MaxPagesPerLeaderboard;
        if (maxPages <= 0) return; // unlimited — no pruning

        var maxEntries = maxPages * 100;
        try
        {
            var deleted = _persistence.PruneAllInstruments(maxEntries, ctx.RegisteredIds);
            if (deleted > 0)
                _log.LogInformation("Pruned {Deleted:N0} excess entries (keeping top {Max:N0} per song + registered users).",
                    deleted, maxEntries);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Entry pruning failed. Will retry next pass.");
        }
    }

    /// <summary>
    /// Clean up expired auth sessions and auto-unregister orphaned accounts.
    /// </summary>
    internal void CleanupSessions()
    {
        try
        {
            var cleaned = _persistence.Meta.CleanupExpiredSessions(DateTime.UtcNow.AddDays(-7));
            if (cleaned > 0)
                _log.LogInformation("Cleaned up {Count} expired/revoked auth session(s).", cleaned);

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
    }
}
