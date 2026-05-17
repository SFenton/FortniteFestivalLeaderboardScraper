using FortniteFestival.Core.Scraping;
using FortniteFestival.Core.Services;
using FSTService.Api;
using FSTService.Auth;
using FSTService.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FSTService.Scraping;

/// <summary>
/// Orchestrates backfill and history reconstruction phases using the
/// <see cref="SongProcessingMachine"/> for batched song-parallel V2 API calls.
/// </summary>
public sealed class BackfillOrchestrator
{
    private readonly BackfillQueue _backfillQueue;
    private readonly HistoryReconstructor _historyReconstructor;
    private readonly RivalsOrchestrator _rivalsOrchestrator;
    private readonly NotificationService _notifications;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly TokenManager _tokenManager;
    private readonly ScrapeProgressTracker _progress;
    private readonly UserSyncProgressTracker _syncTracker;
    private readonly IOptions<ScraperOptions> _options;
    private readonly CyclicalSongMachine _cyclicalMachine;
    private readonly SharedDopPool _pool;
    private readonly BatchResultProcessor _resultProcessor;
    private readonly ScrapeTimePrecomputer _precomputer;
    private readonly ResponseCacheService _leaderboardAllCache;
    private readonly ILogger<BackfillOrchestrator> _log;

    public BackfillOrchestrator(
        BackfillQueue backfillQueue,
        HistoryReconstructor historyReconstructor,
        RivalsOrchestrator rivalsOrchestrator,
        NotificationService notifications,
        GlobalLeaderboardPersistence persistence,
        TokenManager tokenManager,
        ScrapeProgressTracker progress,
        UserSyncProgressTracker syncTracker,
        IOptions<ScraperOptions> options,
        CyclicalSongMachine cyclicalMachine,
        SharedDopPool pool,
        BatchResultProcessor resultProcessor,
        ScrapeTimePrecomputer precomputer,
        [FromKeyedServices("LeaderboardAllCache")] ResponseCacheService leaderboardAllCache,
        ILogger<BackfillOrchestrator> log)
    {
        _backfillQueue = backfillQueue;
        _historyReconstructor = historyReconstructor;
        _rivalsOrchestrator = rivalsOrchestrator;
        _notifications = notifications;
        _persistence = persistence;
        _tokenManager = tokenManager;
        _progress = progress;
        _syncTracker = syncTracker;
        _options = options;
        _cyclicalMachine = cyclicalMachine;
        _pool = pool;
        _resultProcessor = resultProcessor;
        _precomputer = precomputer;
        _leaderboardAllCache = leaderboardAllCache;
        _log = log;
    }

    /// <summary>
    /// Claims API-queued registration backfills and attaches them to the worker-owned
    /// cyclical song machine at low priority, sharing the active DOP/RPS/CDN limiter.
    /// </summary>
    public async Task<int> RunQueuedRegistrationBackfillBatchAsync(
        FestivalService service,
        int maxAccounts,
        CancellationToken ct)
    {
        var deferredBackfills = _persistence.Meta.GetDeferredBackfills();
        if (deferredBackfills.Count == 0)
            return 0;

        var selectedBackfills = deferredBackfills
            .OrderBy(static backfill => backfill.AccountId, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxAccounts))
            .ToList();

        if (selectedBackfills.Count == 0)
            return 0;

        if (service.Songs.Count == 0)
            await service.InitializeAsync();

        var chartedSongIds = service.Songs
            .Select(static song => song.track?.su)
            .Where(static songId => !string.IsNullOrWhiteSpace(songId))
            .Select(static songId => songId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (chartedSongIds.Count == 0)
        {
            _log.LogWarning("Queued registration backfill skipped because no charted songs are loaded.");
            return 0;
        }

        var accessToken = await _tokenManager.GetAccessTokenAsync(ct);
        IReadOnlyList<Persistence.SeasonWindowInfo> seasonWindows = [];
        if (accessToken is not null)
        {
            try
            {
                seasonWindows = await _historyReconstructor.DiscoverSeasonWindowsAsync(
                    accessToken, _tokenManager.AccountId!, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Season window discovery failed during queued registration backfill. Using stored season windows.");
            }
        }
        else
        {
            _log.LogWarning("No access token available for queued registration backfill season discovery. Using stored season windows.");
        }

        if (seasonWindows.Count == 0)
            seasonWindows = _persistence.Meta.GetSeasonWindows();

        var allSeasons = seasonWindows.Select(static window => window.SeasonNumber).ToHashSet();
        var canRunCompleteHistoryRecon = allSeasons.Count > 0;
        var accountIds = selectedBackfills
            .Select(static backfill => backfill.AccountId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        RegisterKnownBandsForAccounts(accountIds);

        var users = new List<UserWorkItem>(accountIds.Length);
        var totalsByAccount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var backfill in selectedBackfills)
        {
            var totalPairs = backfill.TotalSongsToCheck > 0
                ? backfill.TotalSongsToCheck
                : chartedSongIds.Count * GlobalLeaderboardScraper.AllInstruments.Count;

            totalsByAccount[backfill.AccountId] = totalPairs;
            _persistence.Meta.StartBackfill(backfill.AccountId);
            _syncTracker.BeginBackfill(backfill.AccountId, totalPairs);

            users.Add(new UserWorkItem
            {
                AccountId = backfill.AccountId,
                Purposes = canRunCompleteHistoryRecon
                    ? WorkPurpose.Backfill | WorkPurpose.HistoryRecon
                    : WorkPurpose.Backfill,
                AllTimeNeeded = true,
                SeasonsNeeded = canRunCompleteHistoryRecon ? new HashSet<int>(allSeasons) : [],
                AlreadyChecked = _persistence.Meta.GetCheckedBackfillPairs(backfill.AccountId),
            });
        }

        _log.LogInformation(
            "Queued registration backfill attaching {Users} user(s), {Songs} songs, priority=low.",
            users.Count, chartedSongIds.Count);

        try
        {
            _resultProcessor.SetStagingAccounts(accountIds);

            var result = await _cyclicalMachine.AttachAsync(
                users,
                chartedSongIds,
                seasonWindows,
                SongMachineSource.Backfill,
                isHighPriority: false,
                ct: ct);

            _log.LogInformation(
                "Queued registration backfill completed: {Updated} entries, {Sessions} sessions, {ApiCalls} API calls for {Users} users.",
                result.EntriesUpdated, result.SessionsInserted, result.ApiCalls, result.UsersProcessed);

            foreach (var user in users)
            {
                try
                {
                    _resultProcessor.FlushStagedData(user.AccountId);
                    _persistence.Meta.CompleteBackfill(user.AccountId, rankingsPending: true);
                    _rivalsOrchestrator.ComputeForUser(user.AccountId);
                    _precomputer.PrecomputeUser(user.AccountId);
                    _ = _notifications.NotifyBackfillCompleteAsync(user.AccountId);

                    if (user.Purposes.HasFlag(WorkPurpose.HistoryRecon))
                    {
                        var reconStatus = _persistence.Meta.GetHistoryReconStatus(user.AccountId);
                        if (reconStatus is null)
                            _persistence.Meta.EnqueueHistoryRecon(user.AccountId, 0);

                        _persistence.Meta.CompleteHistoryRecon(user.AccountId);
                        _ = _notifications.NotifyHistoryReconCompleteAsync(user.AccountId);
                    }
                    else
                    {
                        EnsureHistoryReconPending(user.AccountId, chartedSongIds.Count);
                    }

                    _syncTracker.Complete(user.AccountId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Post-backfill actions failed for queued registration account {AccountId}.", user.AccountId);
                }
            }

            if (result.EntriesUpdated > 0)
                _leaderboardAllCache.InvalidateAll();

            return users.Count;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Queued registration backfill batch failed. Returning accounts to the deferred queue.");
            foreach (var user in users)
            {
                var totalPairs = totalsByAccount.TryGetValue(user.AccountId, out var total) ? total : chartedSongIds.Count * GlobalLeaderboardScraper.AllInstruments.Count;
                _persistence.Meta.DeferBackfill(user.AccountId, totalPairs, "worker_backfill_retry");
                _syncTracker.BeginQueued(user.AccountId, totalPairs);
            }

            return 0;
        }
        finally
        {
            _resultProcessor.ClearStagingAccounts();
        }
    }

    /// <summary>
    /// Run backfills for any queued accounts (from login/registration) and
    /// also resume any in-progress backfills that were interrupted.
    /// Uses the <see cref="SongProcessingMachine"/> for batched V2 lookups
    /// instead of per-user sequential API calls.
    /// </summary>
    public async Task RunBackfillAsync(FestivalService service, CancellationToken ct)
    {
        var queued = _backfillQueue.DrainAll();
        var pending = _persistence.Meta.GetPendingBackfills();

        var accountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var req in queued) accountIds.Add(req.AccountId);
        foreach (var bf in pending) accountIds.Add(bf.AccountId);

        if (accountIds.Count == 0) return;

        _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.SongMachine);

        var accessToken = await _tokenManager.GetAccessTokenAsync(ct);
        if (accessToken is null)
        {
            _log.LogWarning("No access token available for backfill. Will retry next pass.");
            foreach (var id in accountIds) _backfillQueue.Enqueue(new BackfillRequest(id));
            return;
        }

        var callerAccountId = _tokenManager.AccountId!;

        // Discover season windows for history reconstruction
        _progress.SetSubOperation("discovering_season_windows");
        IReadOnlyList<Persistence.SeasonWindowInfo> seasonWindows;
        try
        {
            seasonWindows = await _historyReconstructor.DiscoverSeasonWindowsAsync(
                accessToken, callerAccountId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Season window discovery failed. Using empty season list.");
            seasonWindows = [];
        }

        if (seasonWindows.Count == 0)
            seasonWindows = _persistence.Meta.GetSeasonWindows();

        var allSeasons = seasonWindows.Select(w => w.SeasonNumber).ToHashSet();
        var canRunCompleteHistoryRecon = allSeasons.Count > 0;

        // Get all charted song IDs
        var chartedSongIds = service.Songs
            .Where(s => s.track?.su is not null)
            .Select(s => s.track.su!)
            .ToList();

        if (chartedSongIds.Count == 0)
        {
            _log.LogWarning("No charted songs available for backfill.");
            return;
        }

        RegisterKnownBandsForAccounts(accountIds);

        // Build user work list — combine backfill + history recon
        _progress.SetSubOperation("building_work_list");
        var users = new List<UserWorkItem>();
        foreach (var accountId in accountIds)
        {
            var alreadyChecked = _persistence.Meta.GetCheckedBackfillPairs(accountId);
            var totalPairs = chartedSongIds.Count * GlobalLeaderboardScraper.AllInstruments.Count;
            _persistence.Meta.EnqueueBackfill(accountId, totalPairs);
            _persistence.Meta.StartBackfill(accountId);

            users.Add(new UserWorkItem
            {
                AccountId = accountId,
                Purposes = canRunCompleteHistoryRecon
                    ? WorkPurpose.Backfill | WorkPurpose.HistoryRecon
                    : WorkPurpose.Backfill,
                AllTimeNeeded = true,
                SeasonsNeeded = canRunCompleteHistoryRecon ? new HashSet<int>(allSeasons) : [],
                AlreadyChecked = alreadyChecked,
            });
        }

        _log.LogInformation(
            "Backfill via SongProcessingMachine: {Users} users, {Songs} songs, {Seasons} seasons.",
            users.Count, chartedSongIds.Count, allSeasons.Count);

        try
        {
            // Enable staging mode so DB writes are buffered until per-user flush
            _resultProcessor.SetStagingAccounts(accountIds);

            _progress.SetSubOperation("processing_songs");
            var result = await _cyclicalMachine.AttachAsync(
                users, chartedSongIds, seasonWindows,
                SongMachineSource.Backfill, isHighPriority: false, ct: ct);

            _log.LogInformation(
                "Backfill complete: {Updated} entries, {Sessions} sessions, {ApiCalls} API calls for {Users} users.",
                result.EntriesUpdated, result.SessionsInserted, result.ApiCalls, result.UsersProcessed);

            // Per-user completion: flush staged data → mark complete → rivals → precompute → notify
            _progress.SetSubOperation("completing_user_actions");
            foreach (var user in users)
            {
                try
                {
                    _resultProcessor.FlushStagedData(user.AccountId);
                    _persistence.Meta.CompleteBackfill(user.AccountId);
                    _rivalsOrchestrator.ComputeForUser(user.AccountId);
                    _precomputer.PrecomputeUser(user.AccountId);
                    _ = _notifications.NotifyBackfillCompleteAsync(user.AccountId);

                    if (!user.Purposes.HasFlag(WorkPurpose.HistoryRecon))
                        EnsureHistoryReconPending(user.AccountId, chartedSongIds.Count);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Post-backfill actions failed for {AccountId}.", user.AccountId);
                }

                // History recon completion
                try
                {
                    if (!user.Purposes.HasFlag(WorkPurpose.HistoryRecon))
                        continue;

                    var reconStatus = _persistence.Meta.GetHistoryReconStatus(user.AccountId);
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

            if (result.EntriesUpdated > 0)
                _leaderboardAllCache.InvalidateAll();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Backfill via SongProcessingMachine failed. Will retry next pass.");
        }
        finally
        {
            _resultProcessor.ClearStagingAccounts();
        }
    }

    /// <summary>
    /// Run history reconstruction for registered users whose backfill is complete
    /// but whose history hasn't been reconstructed yet.
    /// Uses the <see cref="SongProcessingMachine"/> for batched seasonal queries.
    /// </summary>
    public async Task RunHistoryReconAsync(FestivalService service, CancellationToken ct)
    {
        var registeredIds = _persistence.Meta.GetRegisteredAccountIds();
        if (registeredIds.Count == 0) return;

        var accountsToReconstruct = new List<string>();
        foreach (var accountId in registeredIds)
        {
            var backfillStatus = _persistence.Meta.GetBackfillStatus(accountId);
            if (backfillStatus?.Status != "complete") continue;

            var reconStatus = _persistence.Meta.GetHistoryReconStatus(accountId);
            if (reconStatus?.Status == "complete") continue;

            accountsToReconstruct.Add(accountId);
        }

        if (accountsToReconstruct.Count == 0) return;

        _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.SongMachine);

        var accessToken = await _tokenManager.GetAccessTokenAsync(ct);
        if (accessToken is null)
        {
            _log.LogWarning("No access token available for history reconstruction. Will retry next pass.");
            return;
        }

        var callerAccountId = _tokenManager.AccountId!;

        _progress.SetSubOperation("discovering_season_windows");
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

        var allSeasons = seasonWindows.Select(w => w.SeasonNumber).ToHashSet();

        var chartedSongIds = service.Songs
            .Where(s => s.track?.su is not null)
            .Select(s => s.track.su!)
            .ToList();

        if (chartedSongIds.Count == 0) return;

        RegisterKnownBandsForAccounts(accountsToReconstruct);

        _progress.SetSubOperation("building_work_list");
        var users = new List<UserWorkItem>();
        foreach (var accountId in accountsToReconstruct)
        {
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

        _log.LogInformation(
            "History recon via SongProcessingMachine: {Users} users, {Songs} songs, {Seasons} seasons.",
            users.Count, chartedSongIds.Count, allSeasons.Count);

        try
        {
            _progress.SetSubOperation("processing_songs");
            var result = await _cyclicalMachine.AttachAsync(
                users, chartedSongIds, seasonWindows,
                SongMachineSource.HistoryRecon, isHighPriority: false, ct: ct);

            _log.LogInformation(
                "History recon complete: {Sessions} sessions inserted, {ApiCalls} API calls for {Users} users.",
                result.SessionsInserted, result.ApiCalls, result.UsersProcessed);

            _progress.SetSubOperation("completing_user_actions");
            foreach (var user in users)
            {
                try
                {
                    var reconStatus = _persistence.Meta.GetHistoryReconStatus(user.AccountId);
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
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "History recon via SongProcessingMachine failed. Will retry next pass.");
        }
    }

    private void RegisterKnownBandsForAccounts(IEnumerable<string> accountIds)
    {
        var registeredBands = 0;
        foreach (var accountId in accountIds.Distinct(StringComparer.OrdinalIgnoreCase))
            registeredBands += _persistence.Meta.RegisterKnownBandsForAccountActivity(accountId);

        if (registeredBands > 0)
            _log.LogDebug("Registered or refreshed {BandCount} known band(s) for tracked player history processing.", registeredBands);
    }

    private void EnsureHistoryReconPending(string accountId, int totalSongsToProcess)
    {
        var reconStatus = _persistence.Meta.GetHistoryReconStatus(accountId);
        if (reconStatus?.Status != "complete")
            _persistence.Meta.EnqueueHistoryRecon(accountId, totalSongsToProcess);
    }
}
