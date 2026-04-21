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
    private readonly UserSyncProgressTracker _syncTracker;
    private readonly ResponseCacheService _rivalsCache;
    private readonly ILogger<RivalsOrchestrator> _log;

    public RivalsOrchestrator(
        RivalsCalculator calculator,
        GlobalLeaderboardPersistence persistence,
        NotificationService notifications,
        ScrapeProgressTracker progress,
        UserSyncProgressTracker syncTracker,
        [FromKeyedServices("RivalsCache")] ResponseCacheService rivalsCache,
        ILogger<RivalsOrchestrator> log)
    {
        _calculator = calculator;
        _persistence = persistence;
        _notifications = notifications;
        _progress = progress;
        _syncTracker = syncTracker;
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

        // Reset stale completions: users marked 'complete' with 0 rivals found
        // are likely victims of a computation that ran before data was available.
        var resetCount = _persistence.Meta.ResetStaleRivals();
        if (resetCount > 0)
            _log.LogInformation("Reset {Count} stale rivals status (complete with 0 rivals) to pending.", resetCount);

        // Determine who needs computation
        var pending = _persistence.Meta.GetPendingRivalsAccounts();
        var dirtyAccounts = _persistence.Meta.GetDirtyRivalAccounts();
        var pendingSet = new HashSet<string>(pending, StringComparer.OrdinalIgnoreCase);

        // Also include users with dirty instruments (score changes)
        var toCompute = new HashSet<string>(pendingSet, StringComparer.OrdinalIgnoreCase);
        foreach (var userId in dirtyAccounts)
        {
            if (registeredIds.Contains(userId))
                toCompute.Add(userId);
        }

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
        _progress.SetSubOperation("per_song_rivals");
        _log.LogInformation(
            "Computing rivals for {Count} registered user(s). Pending={PendingAccounts}, Dirty={DirtyAccounts}.",
            toCompute.Count,
            pendingSet.Count,
            dirtyAccounts.Count);

        var tasks = toCompute.Select(accountId => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var forceRecompute = pendingSet.Contains(accountId) ||
                (dirtyInstrumentsByUser is not null && dirtyInstrumentsByUser.ContainsKey(accountId));
            var outcome = ComputeForUser(accountId, forceRecompute);
            _progress.ReportPhaseAccountComplete();
            return outcome;
        }, ct)).ToList();

        var outcomes = await Task.WhenAll(tasks);
        _log.LogInformation(
            "Song-rivals outcome summary: skipped={SkippedAccounts}, recomputed={RecomputedAccounts}, outcomes={OutcomeCounts}.",
            outcomes.Count(o => o.WasSkipped),
            outcomes.Count(o => o.WasRecomputed),
            FormatCountSummary(outcomes.GroupBy(o => o.OutcomeCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Compute rivals for a single user (called from parallel Task.Run or directly after backfill).
    /// </summary>
    public RivalsComputeOutcome ComputeForUser(string accountId, bool forceRecompute = false)
    {
        try
        {
            // Ensure a rivals_status row exists — without this, StartRivals/CompleteRivals
            // (which are UPDATEs) silently affect 0 rows when called from BackfillOrchestrator.
            _persistence.Meta.EnsureRivalsStatus(accountId);

            var dirtySongs = _persistence.Meta.GetDirtyRivalSongs(accountId);
            var outcomeCode = RivalsComputeOutcomeCode.ForceRecomputeRequested;

            if (!forceRecompute && dirtySongs.Count > 0)
            {
                var decision = EvaluateDirtySongs(accountId, dirtySongs);
                outcomeCode = decision.OutcomeCode;
                if (!decision.RequiresRecompute)
                {
                    _log.LogInformation(
                        "Song-rivals outcome for {AccountId}: {OutcomeCode} dirtySongs={DirtySongs}.",
                        accountId,
                        decision.OutcomeCode,
                        dirtySongs.Count);
                    return new RivalsComputeOutcome(accountId, decision.OutcomeCode, WasRecomputed: false, DirtySongCount: dirtySongs.Count);
                }
            }
            else if (dirtySongs.Count > 0)
            {
                outcomeCode = RivalsComputeOutcomeCode.ForceRecomputeRequested;
            }

            // Quick pre-scan: count valid instruments to compute total combos for progress tracking
            var totalCombos = _calculator.CountValidCombos(accountId);
            _persistence.Meta.StartRivals(accountId, totalCombos);
            _syncTracker.BeginRivals(accountId, totalCombos);

            var result = _calculator.ComputeRivals(accountId,
                onProgress: combosCompleted =>
                {
                    _syncTracker.ReportRivalsItem(accountId, combosCompleted, rivalsFound: 0);
                });

            _persistence.Meta.ReplaceRivalsData(accountId, result.Rivals, result.Samples);
            var selectionState = _calculator.ComputeSelectionState(accountId);
            _persistence.Meta.ReplaceRivalSelectionState(accountId, selectionState.Fingerprints, selectionState.InstrumentStates);
            _persistence.Meta.ClearAllDirtyRivalSongs(accountId);
            _persistence.Meta.CompleteRivals(accountId, result.CombosComputed, result.Rivals.Count);
            _syncTracker.ReportRivalsItem(accountId, result.CombosComputed, result.Rivals.Count);
            _syncTracker.Complete(accountId);
            _rivalsCache.InvalidateAll();
            _calculator.InvalidateSongGapsCache();

            try { _notifications.NotifyRivalsCompleteAsync(accountId).GetAwaiter().GetResult(); }
            catch { /* best effort */ }

            _log.LogInformation(
                "Song-rivals outcome for {AccountId}: {OutcomeCode} dirtySongs={DirtySongs} combos={Combos} rivals={Rivals} samples={Samples}.",
                accountId,
                outcomeCode,
                dirtySongs.Count,
                result.CombosComputed,
                result.Rivals.Count,
                result.Samples.Count);

            return new RivalsComputeOutcome(accountId, outcomeCode, WasRecomputed: true, DirtySongCount: dirtySongs.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Rivals computation failed for {AccountId}. Will retry next pass.", accountId);
            _persistence.Meta.FailRivals(accountId, ex.Message);
            _syncTracker.Error(accountId, ex.Message);
            return new RivalsComputeOutcome(accountId, RivalsComputeOutcomeCode.Error, WasRecomputed: false, DirtySongCount: 0);
        }
    }

    private DirtySongDecision EvaluateDirtySongs(string accountId, IReadOnlyList<RivalDirtySongRow> dirtySongs)
    {
        var dirtySongsByInstrument = dirtySongs
            .GroupBy(row => row.Instrument, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlySet<string>)group.Select(row => row.SongId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        var dirtyInstruments = new HashSet<string>(dirtySongsByInstrument.Keys, StringComparer.OrdinalIgnoreCase);
        var selectionState = _calculator.ComputeSelectionState(accountId, dirtyInstruments, dirtySongsByInstrument);
        var currentStates = selectionState.InstrumentStates.ToDictionary(state => state.Instrument, StringComparer.OrdinalIgnoreCase);
        var currentFingerprintsByInstrument = selectionState.Fingerprints
            .GroupBy(row => row.Instrument, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToDictionary(row => row.SongId, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        var storedStates = _persistence.Meta.GetRivalInstrumentStates(accountId);

        foreach (var (instrument, songIds) in dirtySongsByInstrument)
        {
            if (!currentStates.TryGetValue(instrument, out var currentState) ||
                !storedStates.TryGetValue(instrument, out var storedState))
            {
                return DirtySongDecision.Recompute(RivalsComputeOutcomeCode.RecomputeMissingBaseline);
            }

            if (currentState.SongCount != storedState.SongCount ||
                currentState.IsEligible != storedState.IsEligible)
            {
                return DirtySongDecision.Recompute(RivalsComputeOutcomeCode.RecomputeEligibilityChanged);
            }

            var storedFingerprints = _persistence.Meta.GetRivalSongFingerprints(accountId, instrument, songIds);
            currentFingerprintsByInstrument.TryGetValue(instrument, out var currentFingerprints);
            currentFingerprints ??= new Dictionary<string, RivalSongFingerprintRow>(StringComparer.OrdinalIgnoreCase);

            foreach (var songId in songIds)
            {
                if (!storedFingerprints.TryGetValue(songId, out var storedFingerprint))
                {
                    return DirtySongDecision.Recompute(RivalsComputeOutcomeCode.RecomputeMissingBaseline);
                }

                if (!currentFingerprints.TryGetValue(songId, out var currentFingerprint) ||
                    currentFingerprint.UserRank != storedFingerprint.UserRank ||
                    !string.Equals(currentFingerprint.NeighborhoodSignature, storedFingerprint.NeighborhoodSignature, StringComparison.Ordinal))
                {
                    return DirtySongDecision.Recompute(RivalsComputeOutcomeCode.RecomputeFingerprintChanged);
                }
            }

            _persistence.Meta.ClearDirtyRivalSongs(accountId, instrument, songIds);
        }

        return DirtySongDecision.Skip();
    }

    private static string FormatCountSummary(IReadOnlyDictionary<string, int> counts)
    {
        if (counts.Count == 0)
            return "none";

        return string.Join(", ",
            counts.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private readonly record struct DirtySongDecision(bool RequiresRecompute, string OutcomeCode)
    {
        public static DirtySongDecision Skip() => new(false, RivalsComputeOutcomeCode.SkipCleanAfterCompare);

        public static DirtySongDecision Recompute(string outcomeCode) => new(true, outcomeCode);
    }

    public readonly record struct RivalsComputeOutcome(string AccountId, string OutcomeCode, bool WasRecomputed, int DirtySongCount)
    {
        public bool WasSkipped => !WasRecomputed && !string.Equals(OutcomeCode, RivalsComputeOutcomeCode.Error, StringComparison.Ordinal);
    }

    private static class RivalsComputeOutcomeCode
    {
        public const string SkipCleanAfterCompare = "skip_clean_after_compare";
        public const string RecomputeMissingBaseline = "recompute_missing_baseline";
        public const string RecomputeFingerprintChanged = "recompute_fingerprint_changed";
        public const string RecomputeEligibilityChanged = "recompute_eligibility_changed";
        public const string ForceRecomputeRequested = "force_recompute_requested";
        public const string Error = "error";
    }
}
