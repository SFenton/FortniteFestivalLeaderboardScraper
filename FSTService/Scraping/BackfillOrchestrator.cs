using FortniteFestival.Core.Scraping;
using FortniteFestival.Core.Services;
using FSTService.Api;
using FSTService.Auth;
using FSTService.Persistence;
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
    private readonly IOptions<ScraperOptions> _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly SharedDopPool _pool;
    private readonly ILogger<BackfillOrchestrator> _log;

    public BackfillOrchestrator(
        BackfillQueue backfillQueue,
        HistoryReconstructor historyReconstructor,
        RivalsOrchestrator rivalsOrchestrator,
        NotificationService notifications,
        GlobalLeaderboardPersistence persistence,
        TokenManager tokenManager,
        ScrapeProgressTracker progress,
        IOptions<ScraperOptions> options,
        IServiceProvider serviceProvider,
        SharedDopPool pool,
        ILogger<BackfillOrchestrator> log)
    {
        _backfillQueue = backfillQueue;
        _historyReconstructor = historyReconstructor;
        _rivalsOrchestrator = rivalsOrchestrator;
        _notifications = notifications;
        _persistence = persistence;
        _tokenManager = tokenManager;
        _progress = progress;
        _options = options;
        _serviceProvider = serviceProvider;
        _pool = pool;
        _log = log;
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

        var allSeasons = seasonWindows.Select(w => w.SeasonNumber).ToHashSet();

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
                Purposes = WorkPurpose.Backfill | WorkPurpose.HistoryRecon,
                AllTimeNeeded = true,
                SeasonsNeeded = new HashSet<int>(allSeasons),
                AlreadyChecked = alreadyChecked,
            });
        }

        _log.LogInformation(
            "Backfill via SongProcessingMachine: {Users} users, {Songs} songs, {Seasons} seasons.",
            users.Count, chartedSongIds.Count, allSeasons.Count);

        try
        {
            _progress.SetSubOperation("processing_songs");
            var machine = _serviceProvider.GetRequiredService<SongProcessingMachine>();
            var result = await machine.RunAsync(
                chartedSongIds, users, seasonWindows,
                accessToken, callerAccountId,
                _pool, isHighPriority: false,
                _options.Value.LookupBatchSize, reportProgress: true, ct);

            _log.LogInformation(
                "Backfill complete: {Updated} entries, {Sessions} sessions, {ApiCalls} API calls for {Users} users.",
                result.EntriesUpdated, result.SessionsInserted, result.ApiCalls, result.UsersProcessed);

            // Per-user completion actions
            _progress.SetSubOperation("completing_user_actions");
            foreach (var user in users)
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

                // History recon completion
                try
                {
                    var reconStatus = _persistence.Meta.GetHistoryReconStatus(user.AccountId);
                    if (reconStatus is null)
                        _persistence.Meta.EnqueueHistoryRecon(user.AccountId, 0);

                    _persistence.Meta.CompleteHistoryRecon(user.AccountId);
                    _ = _notifications.NotifyHistoryReconCompleteAsync(user.AccountId);
                    _ = _notifications.NotifyPersonalDbReadyAsync(user.AccountId);
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
            _log.LogError(ex, "Backfill via SongProcessingMachine failed. Will retry next pass.");
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
            var machine = _serviceProvider.GetRequiredService<SongProcessingMachine>();
            var result = await machine.RunAsync(
                chartedSongIds, users, seasonWindows,
                accessToken, callerAccountId,
                _pool, isHighPriority: false,
                _options.Value.LookupBatchSize, reportProgress: true, ct);

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
}
