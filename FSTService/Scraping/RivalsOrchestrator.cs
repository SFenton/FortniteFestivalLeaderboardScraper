using FSTService.Api;
using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// Orchestrates rivals computation for registered users.
/// Runs as part of the post-scrape pipeline and after backfill completion.
/// </summary>
public sealed class RivalsOrchestrator
{
    private readonly RivalsCalculator _calculator;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly NotificationService _notifications;
    private readonly ScrapeProgressTracker _progress;
    private readonly ResponseCacheService _rivalsCache;
    private readonly ILogger<RivalsOrchestrator> _log;

    public RivalsOrchestrator(
        RivalsCalculator calculator,
        GlobalLeaderboardPersistence persistence,
        NotificationService notifications,
        ScrapeProgressTracker progress,
        [FromKeyedServices("RivalsCache")] ResponseCacheService rivalsCache,
        ILogger<RivalsOrchestrator> log)
    {
        _calculator = calculator;
        _persistence = persistence;
        _notifications = notifications;
        _progress = progress;
        _rivalsCache = rivalsCache;
        _log = log;
    }

    /// <summary>
    /// Compute rivals for all registered users that need (re)computation.
    /// Runs in parallel across users (each reads instrument DBs under WAL).
    /// </summary>
    public async Task ComputeAllAsync(
        HashSet<string> registeredIds,
        IReadOnlyDictionary<string, HashSet<string>>? dirtyInstrumentsByUser,
        CancellationToken ct)
    {
        if (registeredIds.Count == 0)
            return;

        // Ensure every registered user has a RivalsStatus row
        foreach (var id in registeredIds)
            _persistence.Meta.EnsureRivalsStatus(id);

        // Determine who needs computation
        var pending = _persistence.Meta.GetPendingRivalsAccounts();

        // Also include users with dirty instruments (score changes)
        var toCompute = new HashSet<string>(pending, StringComparer.OrdinalIgnoreCase);
        if (dirtyInstrumentsByUser is not null)
        {
            foreach (var (userId, _) in dirtyInstrumentsByUser)
            {
                if (registeredIds.Contains(userId))
                    toCompute.Add(userId);
            }
        }

        if (toCompute.Count == 0)
            return;

        _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.ComputingRivals);
        _progress.BeginPhaseProgress(totalItems: 0, totalAccounts: toCompute.Count);
        _log.LogInformation("Computing rivals for {Count} registered user(s).", toCompute.Count);

        var tasks = toCompute.Select(accountId => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            ComputeForUser(accountId, dirtyInstrumentsByUser);
            _progress.ReportPhaseAccountComplete();
        }, ct)).ToList();

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Compute rivals for a single user (called from parallel Task.Run or directly after backfill).
    /// </summary>
    public void ComputeForUser(
        string accountId,
        IReadOnlyDictionary<string, HashSet<string>>? dirtyInstrumentsByUser = null)
    {
        try
        {
            IReadOnlySet<string>? dirtyInstruments = null;
            if (dirtyInstrumentsByUser is not null &&
                dirtyInstrumentsByUser.TryGetValue(accountId, out var dirty))
            {
                dirtyInstruments = dirty;
            }

            // Quick pre-scan: count valid instruments to compute total combos for progress tracking
            var totalCombos = _calculator.CountValidCombos(accountId, dirtyInstruments);
            _persistence.Meta.StartRivals(accountId, totalCombos);

            var result = _calculator.ComputeRivals(accountId, dirtyInstruments);

            _persistence.Meta.ReplaceRivalsData(accountId, result.Rivals, result.Samples);
            _persistence.Meta.CompleteRivals(accountId, result.CombosComputed, result.Rivals.Count);
            _rivalsCache.InvalidateAll();
            _calculator.InvalidateSongGapsCache();

            try { _notifications.NotifyRivalsCompleteAsync(accountId).GetAwaiter().GetResult(); }
            catch { /* best effort */ }

            _log.LogInformation(
                "Computed rivals for {AccountId}: {Combos} combo(s), {Rivals} rival rows, {Samples} sample rows.",
                accountId, result.CombosComputed, result.Rivals.Count, result.Samples.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Rivals computation failed for {AccountId}. Will retry next pass.", accountId);
            _persistence.Meta.FailRivals(accountId, ex.Message);
        }
    }
}
