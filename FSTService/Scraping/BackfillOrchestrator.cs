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
    private readonly RegistrationMutationCoordinator
        _registrationMutations;
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
        RegistrationMutationCoordinator registrationMutations,
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
        _registrationMutations = registrationMutations;
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
        var queuedBackfills = _persistence.Meta.GetDeferredBackfills()
            .Concat(_persistence.Meta.GetPendingBackfills())
            .GroupBy(
                static backfill => backfill.AccountId,
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
        if (queuedBackfills.Count == 0)
            return 0;

        var selectedBackfills = queuedBackfills
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

        using var registrationLease =
            _registrationMutations.AcquireLease(ct);

        var opts = _options.Value;
        var foregroundRegistration = opts.RegistrationBackfillMode == RegistrationBackfillMode.ForegroundEpicExclusive;

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
        var canRunCompleteHistoryRecon = opts.RegistrationBackfillIncludeHistoryRecon && allSeasons.Count > 0;
        var historyWindowFingerprint = HistoryReconstructor.ComputeWindowFingerprint(
            seasonWindows);
        var expectedHistoryPairs =
            chartedSongIds.Count * GlobalLeaderboardScraper.AllInstruments.Count;
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
            var historyStatus = _persistence.Meta.GetHistoryReconStatus(
                backfill.AccountId);
            var displayProgress = _persistence.Meta.GetBackfillSongProgress(
                backfill.AccountId,
                backfill.SongsChecked,
                totalPairs);
            var backgroundRefresh =
                BackfillSyncClassification.IsBackgroundRefresh(
                    backfill,
                    historyStatus,
                    displayProgress);
            if (backgroundRefresh
                && !BackfillDeferredReasons.IsCatalogRefresh(
                    backfill.DeferredReason))
            {
                _persistence.Meta.DeferBackfill(
                    backfill.AccountId,
                    totalPairs,
                    BackfillDeferredReasons.CatalogRefreshQueue);
            }

            totalsByAccount[backfill.AccountId] = totalPairs;
            _persistence.Meta.StartBackfill(backfill.AccountId);
            _syncTracker.BeginBackfill(
                backfill.AccountId,
                totalPairs,
                backgroundRefresh);
            var historyAdmissionRevision = canRunCompleteHistoryRecon
                ? _persistence.Meta.AdmitHistoryRecon(
                    backfill.AccountId,
                    expectedHistoryPairs,
                    HistoryReconstructor.CurrentReconstructionVersion,
                    historyWindowFingerprint)
                : 0;
            var backfillChecked = _persistence.Meta.GetCheckedBackfillPairs(
                backfill.AccountId);
            var historyProcessed = canRunCompleteHistoryRecon
                ? _persistence.Meta.GetProcessedHistoryReconPairs(
                    backfill.AccountId,
                    HistoryReconstructor.CurrentReconstructionVersion,
                    historyWindowFingerprint,
                    historyAdmissionRevision)
                : null;

            users.Add(new UserWorkItem
            {
                AccountId = backfill.AccountId,
                Purposes = canRunCompleteHistoryRecon
                    ? WorkPurpose.Backfill | WorkPurpose.HistoryRecon
                    : WorkPurpose.Backfill,
                AllTimeNeeded = true,
                SeasonsNeeded = canRunCompleteHistoryRecon ? new HashSet<int>(allSeasons) : [],
                BackfillAlreadyChecked = backfillChecked,
                HistoryAlreadyProcessed = historyProcessed,
                HistoryReconstructionVersion = canRunCompleteHistoryRecon
                    ? HistoryReconstructor.CurrentReconstructionVersion
                    : 0,
                HistoryWindowFingerprint = canRunCompleteHistoryRecon
                    ? historyWindowFingerprint
                    : "",
                HistoryAdmissionRevision = historyAdmissionRevision,
            });
        }

        _log.LogInformation(
            "Queued registration backfill attaching {Users} user(s), {Songs} songs, mode={Mode}, priority={Priority}.",
            users.Count, chartedSongIds.Count, opts.RegistrationBackfillMode,
            foregroundRegistration ? "high" : "low");

        IDisposable? foregroundLease = null;
        try
        {
            foregroundLease = foregroundRegistration
                ? _pool.TrafficCoordinator.BeginForegroundRegistration()
                : null;

            _resultProcessor.SetStagingAccounts(accountIds);

            var result = await _cyclicalMachine.AttachAsync(
                users,
                chartedSongIds,
                seasonWindows,
                SongMachineSource.Backfill,
                isHighPriority: foregroundRegistration,
                ct: ct,
                epicTrafficKind: foregroundRegistration
                    ? EpicTrafficKind.ForegroundRegistration
                    : EpicTrafficKind.Background);
            foregroundLease?.Dispose();
            foregroundLease = null;

            _log.LogInformation(
                "Queued registration backfill completed: {Updated} entries, {Sessions} sessions, {ApiCalls} API calls for {Users} users.",
                result.EntriesUpdated, result.SessionsInserted, result.ApiCalls, result.UsersProcessed);

            var completionFailed = false;
            foreach (var user in users)
            {
                try
                {
                    var stagedCommitted = user.Purposes.HasFlag(WorkPurpose.HistoryRecon)
                        ? _resultProcessor.FlushStagedData(
                            user.AccountId,
                            user.HistoryReconstructionVersion,
                            user.HistoryWindowFingerprint,
                            user.HistoryAdmissionRevision)
                        : FlushBackfillOnly(user.AccountId);
                    if (!stagedCommitted)
                        throw new InvalidOperationException(
                            $"Staged data identity was stale for {user.AccountId}.");
                    if (!TryCompleteBackfill(user.AccountId, chartedSongIds))
                        throw new InvalidOperationException(
                            $"Backfill coverage remained incomplete for {user.AccountId}.");
                    _persistence.Meta.QueueRivalsRecompute(user.AccountId);
                    _precomputer.PrecomputeUser(user.AccountId);
                    _ = _notifications.NotifyBackfillCompleteAsync(user.AccountId);

                    if (user.Purposes.HasFlag(WorkPurpose.HistoryRecon))
                    {
                        if (TryCompleteHistoryRecon(user, chartedSongIds))
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
                    completionFailed = true;
                    _log.LogWarning(ex, "Post-backfill actions failed for queued registration account {AccountId}.", user.AccountId);
                    var totalPairs = totalsByAccount.TryGetValue(
                        user.AccountId,
                        out var total)
                        ? total
                        : chartedSongIds.Count
                          * GlobalLeaderboardScraper.AllInstruments.Count;
                    _persistence.Meta.DeferBackfill(
                        user.AccountId,
                        totalPairs,
                        "worker_backfill_completion_retry");
                    _syncTracker.BeginQueued(user.AccountId, totalPairs);
                }
            }

            if (result.EntriesUpdated > 0)
            {
                _log.LogDebug(
                    "Preserving published leaderboard cache after {UpdatedEntries} backfill update(s); frozen={Frozen}.",
                    result.EntriesUpdated,
                    _leaderboardAllCache.IsFrozen);
            }

            return completionFailed ? 0 : users.Count;
        }
        catch (OperationCanceledException)
        {
            _resultProcessor.DiscardStagedData(accountIds);
            throw;
        }
        catch (Exception ex)
        {
            _resultProcessor.DiscardStagedData(accountIds);
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
            foregroundLease?.Dispose();
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
        using var registrationLease =
            _registrationMutations.AcquireLease(ct);
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
        var historyWindowFingerprint = HistoryReconstructor.ComputeWindowFingerprint(
            seasonWindows);

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
        var expectedHistoryPairs =
            chartedSongIds.Count * GlobalLeaderboardScraper.AllInstruments.Count;
        foreach (var accountId in accountIds)
        {
            var backfillChecked = _persistence.Meta.GetCheckedBackfillPairs(accountId);
            var totalPairs = chartedSongIds.Count * GlobalLeaderboardScraper.AllInstruments.Count;
            _persistence.Meta.EnqueueBackfill(accountId, totalPairs);
            _persistence.Meta.StartBackfill(accountId);
            var historyAdmissionRevision = canRunCompleteHistoryRecon
                ? _persistence.Meta.AdmitHistoryRecon(
                    accountId,
                    expectedHistoryPairs,
                    HistoryReconstructor.CurrentReconstructionVersion,
                    historyWindowFingerprint)
                : 0;
            var historyProcessed = canRunCompleteHistoryRecon
                ? _persistence.Meta.GetProcessedHistoryReconPairs(
                    accountId,
                    HistoryReconstructor.CurrentReconstructionVersion,
                    historyWindowFingerprint,
                    historyAdmissionRevision)
                : null;

            users.Add(new UserWorkItem
            {
                AccountId = accountId,
                Purposes = canRunCompleteHistoryRecon
                    ? WorkPurpose.Backfill | WorkPurpose.HistoryRecon
                    : WorkPurpose.Backfill,
                AllTimeNeeded = true,
                SeasonsNeeded = canRunCompleteHistoryRecon ? new HashSet<int>(allSeasons) : [],
                BackfillAlreadyChecked = backfillChecked,
                HistoryAlreadyProcessed = historyProcessed,
                HistoryReconstructionVersion = canRunCompleteHistoryRecon
                    ? HistoryReconstructor.CurrentReconstructionVersion
                    : 0,
                HistoryWindowFingerprint = canRunCompleteHistoryRecon
                    ? historyWindowFingerprint
                    : "",
                HistoryAdmissionRevision = historyAdmissionRevision,
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
                    var stagedCommitted = user.Purposes.HasFlag(WorkPurpose.HistoryRecon)
                        ? _resultProcessor.FlushStagedData(
                            user.AccountId,
                            user.HistoryReconstructionVersion,
                            user.HistoryWindowFingerprint,
                            user.HistoryAdmissionRevision)
                        : FlushBackfillOnly(user.AccountId);
                    if (!stagedCommitted)
                        throw new InvalidOperationException(
                            $"Staged data identity was stale for {user.AccountId}.");
                    if (!TryCompleteBackfill(user.AccountId, chartedSongIds))
                        throw new InvalidOperationException(
                            $"Backfill coverage remained incomplete for {user.AccountId}.");
                    _persistence.Meta.QueueRivalsRecompute(user.AccountId);
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

                    if (TryCompleteHistoryRecon(user, chartedSongIds))
                        _ = _notifications.NotifyHistoryReconCompleteAsync(user.AccountId);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Post-history-recon actions failed for {AccountId}.", user.AccountId);
                }
            }

            if (result.EntriesUpdated > 0)
            {
                _log.LogDebug(
                    "Preserving published leaderboard cache after {UpdatedEntries} backfill update(s); frozen={Frozen}.",
                    result.EntriesUpdated,
                    _leaderboardAllCache.IsFrozen);
            }
        }
        catch (OperationCanceledException)
        {
            _resultProcessor.DiscardStagedData(accountIds);
            throw;
        }
        catch (Exception ex)
        {
            _resultProcessor.DiscardStagedData(accountIds);
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
        using var registrationLease =
            _registrationMutations.AcquireLease(ct);
        var registeredIds = _persistence.Meta.GetRegisteredAccountIds();
        if (registeredIds.Count == 0) return;

        var accountsToReconstruct = new List<string>();
        foreach (var accountId in registeredIds)
        {
            var backfillStatus = _persistence.Meta.GetBackfillStatus(accountId);
            if (backfillStatus?.Status != "complete") continue;

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
        var historyWindowFingerprint = HistoryReconstructor.ComputeWindowFingerprint(
            seasonWindows);
        var chartedSongIds = service.Songs
            .Select(static song => song.track?.su)
            .Where(static songId => !string.IsNullOrWhiteSpace(songId))
            .Select(static songId => songId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (chartedSongIds.Count == 0)
            return;

        accountsToReconstruct = accountsToReconstruct
            .Where(accountId =>
            {
                var status = _persistence.Meta.GetHistoryReconStatus(accountId);
                if (status?.Status != "complete"
                    || status.ReconstructionVersion
                    != HistoryReconstructor.CurrentReconstructionVersion
                    || !string.Equals(
                        status.WindowFingerprint,
                        historyWindowFingerprint,
                        StringComparison.Ordinal))
                {
                    return true;
                }

                var processed = _persistence.Meta.GetProcessedHistoryReconPairs(
                    accountId,
                    status.ReconstructionVersion,
                    status.WindowFingerprint,
                    status.AdmissionRevision);
                return !HasCurrentHistoryCompletion(
                    status,
                    historyWindowFingerprint,
                    chartedSongIds,
                    processed);
            })
            .ToList();

        if (accountsToReconstruct.Count == 0)
            return;

        RegisterKnownBandsForAccounts(accountsToReconstruct);

        _progress.SetSubOperation("building_work_list");
        var users = new List<UserWorkItem>();
        var expectedHistoryPairs =
            chartedSongIds.Count * GlobalLeaderboardScraper.AllInstruments.Count;
        foreach (var accountId in accountsToReconstruct)
        {
            var historyAdmissionRevision = _persistence.Meta.AdmitHistoryRecon(
                accountId,
                expectedHistoryPairs,
                HistoryReconstructor.CurrentReconstructionVersion,
                historyWindowFingerprint);
            var alreadyProcessed = _persistence.Meta.GetProcessedHistoryReconPairs(
                accountId,
                HistoryReconstructor.CurrentReconstructionVersion,
                historyWindowFingerprint,
                historyAdmissionRevision);
            users.Add(new UserWorkItem
            {
                AccountId = accountId,
                Purposes = WorkPurpose.HistoryRecon,
                AllTimeNeeded = false,
                SeasonsNeeded = new HashSet<int>(allSeasons),
                HistoryAlreadyProcessed = alreadyProcessed,
                HistoryReconstructionVersion =
                    HistoryReconstructor.CurrentReconstructionVersion,
                HistoryWindowFingerprint = historyWindowFingerprint,
                HistoryAdmissionRevision = historyAdmissionRevision,
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
                    if (TryCompleteHistoryRecon(user, chartedSongIds))
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

    private bool TryCompleteHistoryRecon(
        UserWorkItem user,
        IReadOnlyCollection<string> chartedSongIds)
    {
        if (user.HistoryReconstructionVersion <= 0
            || string.IsNullOrWhiteSpace(user.HistoryWindowFingerprint))
        {
            _persistence.Meta.FailHistoryRecon(
                user.AccountId,
                "History reconstruction identity was missing.",
                user.HistoryReconstructionVersion,
                user.HistoryWindowFingerprint,
                user.HistoryAdmissionRevision);
            return false;
        }

        var processed = _persistence.Meta.GetProcessedHistoryReconPairs(
            user.AccountId,
            user.HistoryReconstructionVersion,
            user.HistoryWindowFingerprint,
            user.HistoryAdmissionRevision);
        if (!HasExpectedPairCoverage(chartedSongIds, processed))
        {
            var expectedHistoryPairs =
                chartedSongIds.Count * GlobalLeaderboardScraper.AllInstruments.Count;
            _persistence.Meta.FailHistoryRecon(
                user.AccountId,
                $"History reconstruction incomplete: {processed.Count}/{expectedHistoryPairs} song/instrument pairs.",
                user.HistoryReconstructionVersion,
                user.HistoryWindowFingerprint,
                user.HistoryAdmissionRevision);
            return false;
        }

        _persistence.Meta.CompleteHistoryRecon(
            user.AccountId,
            user.HistoryReconstructionVersion,
            user.HistoryWindowFingerprint,
            user.HistoryAdmissionRevision);
        return true;
    }

    private bool FlushBackfillOnly(string accountId)
    {
        _resultProcessor.FlushStagedData(accountId);
        return true;
    }

    private bool TryCompleteBackfill(
        string accountId,
        IReadOnlyCollection<string> chartedSongIds)
    {
        var expected = chartedSongIds
            .SelectMany(songId =>
                GlobalLeaderboardScraper.AllInstruments.Select(
                    instrument => (SongId: songId, Instrument: instrument)))
            .ToHashSet();
        var checkedPairs = _persistence.Meta.GetCheckedBackfillPairs(accountId);
        if (!expected.IsSubsetOf(checkedPairs))
        {
            _persistence.Meta.EnqueueBackfill(accountId, expected.Count);
            return false;
        }

        _persistence.Meta.CompleteBackfill(
            accountId,
            rankingsPending: true);
        return true;
    }

    internal static bool HasExpectedPairCoverage(
        IReadOnlyCollection<string> chartedSongIds,
        IReadOnlySet<(string SongId, string Instrument)> completedPairs)
    {
        foreach (var songId in chartedSongIds)
        foreach (var instrument in GlobalLeaderboardScraper.AllInstruments)
        {
            if (!completedPairs.Contains((songId, instrument)))
                return false;
        }

        return true;
    }

    internal static bool HasCurrentHistoryCompletion(
        HistoryReconStatusInfo? status,
        string activeWindowFingerprint,
        IReadOnlyCollection<string> chartedSongIds,
        IReadOnlySet<(string SongId, string Instrument)> completedPairs)
    {
        return status?.Status == "complete"
            && status.ReconstructionVersion
                == HistoryReconstructor.CurrentReconstructionVersion
            && string.Equals(
                status.WindowFingerprint,
                activeWindowFingerprint,
                StringComparison.Ordinal)
            && HasExpectedPairCoverage(chartedSongIds, completedPairs);
    }

    private void RegisterKnownBandsForAccounts(IEnumerable<string> accountIds)
    {
        var registeredBands = _persistence.Meta.RegisterKnownBandsForAccountActivities(
            accountIds);

        if (registeredBands > 0)
            _log.LogDebug("Registered or refreshed {BandCount} known band(s) for tracked player history processing.", registeredBands);
    }

    private void EnsureHistoryReconPending(string accountId, int totalSongsToProcess)
    {
        var reconStatus = _persistence.Meta.GetHistoryReconStatus(accountId);
        if (reconStatus is null)
            _persistence.Meta.EnqueueHistoryRecon(accountId, totalSongsToProcess);
    }
}
