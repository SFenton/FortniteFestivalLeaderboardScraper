using FortniteFestival.Core.Services;
using FSTService.Api;
using FSTService.Auth;
using FSTService.Persistence;
using Microsoft.Extensions.Options;

namespace FSTService.Scraping;

/// <summary>
/// Orchestrates backfill and history reconstruction phases.
/// Extracted from <see cref="ScraperWorker"/> to isolate the per-user
/// enrichment lifecycle from the global scrape loop.
/// </summary>
public sealed class BackfillOrchestrator
{
    private readonly ScoreBackfiller _backfiller;
    private readonly BackfillQueue _backfillQueue;
    private readonly HistoryReconstructor _historyReconstructor;
    private readonly PersonalDbBuilder _personalDbBuilder;
    private readonly RivalsOrchestrator _rivalsOrchestrator;
    private readonly NotificationService _notifications;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly TokenManager _tokenManager;
    private readonly ScrapeProgressTracker _progress;
    private readonly IOptions<ScraperOptions> _options;
    private readonly ILogger<BackfillOrchestrator> _log;

    public BackfillOrchestrator(
        ScoreBackfiller backfiller,
        BackfillQueue backfillQueue,
        HistoryReconstructor historyReconstructor,
        PersonalDbBuilder personalDbBuilder,
        RivalsOrchestrator rivalsOrchestrator,
        NotificationService notifications,
        GlobalLeaderboardPersistence persistence,
        TokenManager tokenManager,
        ScrapeProgressTracker progress,
        IOptions<ScraperOptions> options,
        ILogger<BackfillOrchestrator> log)
    {
        _backfiller = backfiller;
        _backfillQueue = backfillQueue;
        _historyReconstructor = historyReconstructor;
        _personalDbBuilder = personalDbBuilder;
        _rivalsOrchestrator = rivalsOrchestrator;
        _notifications = notifications;
        _persistence = persistence;
        _tokenManager = tokenManager;
        _progress = progress;
        _options = options;
        _log = log;
    }

    /// <summary>
    /// Run backfills for any queued accounts (from login/registration) and
    /// also resume any in-progress backfills that were interrupted.
    /// </summary>
    public async Task RunBackfillAsync(FestivalService service, CancellationToken ct)
    {
        var queued = _backfillQueue.DrainAll();
        var pending = _persistence.Meta.GetPendingBackfills();

        var accountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var req in queued) accountIds.Add(req.AccountId);
        foreach (var bf in pending) accountIds.Add(bf.AccountId);

        if (accountIds.Count == 0) return;

        _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.BackfillingScores);

        var accessToken = await _tokenManager.GetAccessTokenAsync(ct);
        if (accessToken is null)
        {
            _log.LogWarning("No access token available for backfill. Will retry next pass.");
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

                if (found > 0)
                {
                    try
                    {
                        // Compute rivals now that we have full score data
                        _rivalsOrchestrator.ComputeForUser(accountId);

                        _personalDbBuilder.RebuildForAccounts(
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { accountId },
                            _persistence.Meta);

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

    /// <summary>
    /// Run history reconstruction for registered users whose backfill is complete
    /// but whose history hasn't been reconstructed yet.
    /// </summary>
    public async Task RunHistoryReconAsync(CancellationToken ct)
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

        _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.ReconstructingHistory);

        var accessToken = await _tokenManager.GetAccessTokenAsync(ct);
        if (accessToken is null)
        {
            _log.LogWarning("No access token available for history reconstruction. Will retry next pass.");
            return;
        }

        var callerAccountId = _tokenManager.AccountId!;

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

        var dop = _options.Value.DegreeOfParallelism;
        int initialDop = Math.Max(1, dop / 2);
        int maxDop = dop * 2;
        using var sharedLimiter = new AdaptiveConcurrencyLimiter(initialDop, minDop: 2, maxDop: maxDop, _log);
        _progress.SetAdaptiveLimiter(sharedLimiter);

        _log.LogInformation(
            "Reconstructing history for {Count} account(s) in parallel with shared limiter (initial DOP={InitialDop}, max={MaxDop}).",
            accountsToReconstruct.Count, initialDop, maxDop);

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

                    try
                    {
                        _personalDbBuilder.RebuildForAccounts(
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { accountId },
                            _persistence.Meta);

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
}
