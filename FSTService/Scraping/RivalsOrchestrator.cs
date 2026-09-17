using System.Collections.Concurrent;
using System.Diagnostics;
using FSTService.Api;
using FSTService.Persistence;
using Microsoft.Extensions.Options;

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
    private readonly ScraperOptions _options;

    public RivalsOrchestrator(
        RivalsCalculator calculator,
        GlobalLeaderboardPersistence persistence,
        NotificationService notifications,
        ScrapeProgressTracker progress,
        UserSyncProgressTracker syncTracker,
        [FromKeyedServices("RivalsCache")] ResponseCacheService rivalsCache,
        ILogger<RivalsOrchestrator> log,
        IOptions<ScraperOptions>? options = null)
    {
        _calculator = calculator;
        _persistence = persistence;
        _notifications = notifications;
        _progress = progress;
        _syncTracker = syncTracker;
        _rivalsCache = rivalsCache;
        _log = log;
        _options = options?.Value ?? new ScraperOptions();
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

        var accounts = toCompute
            .OrderBy(static accountId => accountId,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.ComputingRivals);
        _progress.BeginPhaseProgress(
            totalItems: 0,
            totalAccounts: accounts.Length);
        _progress.SetSubOperation("preloading_rivals_scores");
        var preloadStopwatch = Stopwatch.StartNew();
        var preparedByAccount =
            await _calculator.PrepareCurrentScoresForAccountsAsync(
                accounts,
                ct);
        preloadStopwatch.Stop();
        var preparedScoreCount = preparedByAccount.Values.Sum(
            static prepared => prepared.ScoreCount);
        _log.LogInformation(
            "Preloaded current rivals scores for {AccountCount:N0} account(s): {ScoreCount:N0} score row(s) in {ElapsedMs:N3} ms.",
            accounts.Length,
            preparedScoreCount,
            preloadStopwatch.Elapsed.TotalMilliseconds);

        var maxDegreeOfParallelism = Math.Clamp(
            _options.RivalsMaxDegreeOfParallelism,
            1,
            accounts.Length);
        _progress.SetSubOperation("per_song_rivals");
        _log.LogInformation(
            "Computing rivals for {Count} registered user(s) with maxDegree={MaxDegree}. Pending={PendingAccounts}, Dirty={DirtyAccounts}.",
            accounts.Length,
            maxDegreeOfParallelism,
            pendingSet.Count,
            dirtyAccounts.Count);

        var outcomes = new ConcurrentBag<RivalsComputeOutcome>();
        await Parallel.ForEachAsync(
            accounts,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxDegreeOfParallelism,
                CancellationToken = ct,
            },
            (accountId, innerCt) =>
            {
                innerCt.ThrowIfCancellationRequested();
                var forceRecompute = pendingSet.Contains(accountId) ||
                    (dirtyInstrumentsByUser is not null &&
                     dirtyInstrumentsByUser.ContainsKey(accountId));
                preparedByAccount.TryGetValue(
                    accountId,
                    out var preparedUserData);
                outcomes.Add(
                    ComputeForUser(
                        accountId,
                        forceRecompute,
                        preparedUserData,
                        innerCt));
                _progress.ReportPhaseAccountComplete();
                return ValueTask.CompletedTask;
            });
        var outcomeArray = outcomes.ToArray();
        _log.LogInformation(
            "Song-rivals outcome summary: skipped={SkippedAccounts}, recomputed={RecomputedAccounts}, outcomes={OutcomeCounts}.",
            outcomeArray.Count(o => o.WasSkipped),
            outcomeArray.Count(o => o.WasRecomputed),
            FormatCountSummary(outcomeArray.GroupBy(o => o.OutcomeCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Compute rivals for a single user (called from parallel Task.Run or directly after backfill).
    /// </summary>
    public RivalsComputeOutcome ComputeForUser(
        string accountId,
        bool forceRecompute = false,
        RivalsPreparedUserData? preparedUserData = null,
        CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            // Ensure a rivals_status row exists — without this, StartRivals/CompleteRivals
            // (which are UPDATEs) silently affect 0 rows when called from BackfillOrchestrator.
            _persistence.Meta.EnsureRivalsStatus(accountId);

            var dirtySongs = _persistence.Meta.GetDirtyRivalSongs(accountId);
            var outcomeCode = RivalsComputeOutcomeCode.ForceRecomputeRequested;

            if (!forceRecompute && dirtySongs.Count > 0)
            {
                var decision = EvaluateDirtySongs(
                    accountId,
                    dirtySongs,
                    preparedUserData?.ScoresByInstrument,
                    ct);
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

            var totalCombos = preparedUserData?.TotalCombos
                ?? _calculator.CountValidCombos(accountId);
            _persistence.Meta.StartRivals(accountId, totalCombos);
            _syncTracker.BeginRivals(accountId, totalCombos);

            var result = _calculator.ComputeRivals(accountId,
                onProgress: combosCompleted =>
                {
                    _syncTracker.ReportRivalsItem(accountId, combosCompleted, rivalsFound: 0);
                },
                preparedScoresByInstrument:
                    preparedUserData?.ScoresByInstrument,
                ct: ct);

            ct.ThrowIfCancellationRequested();
            _persistence.Meta.ReplaceRivalsData(accountId, result.Rivals, result.Samples);
            var selectionState = _calculator.ComputeSelectionState(
                accountId,
                preparedScoresByInstrument:
                    preparedUserData?.ScoresByInstrument,
                ct: ct);
            ct.ThrowIfCancellationRequested();
            _persistence.Meta.ReplaceRivalSelectionState(accountId, selectionState.Fingerprints, selectionState.InstrumentStates);
            _persistence.Meta.ClearAllDirtyRivalSongs(accountId);
            var completed = _persistence.Meta.CompleteRivals(accountId, result.CombosComputed, result.Rivals.Count);
            _syncTracker.ReportRivalsItem(accountId, result.CombosComputed, result.Rivals.Count);
            if (completed)
                _syncTracker.Complete(accountId);
            _log.LogDebug(
                "Preserving published rivals cache for {AccountId}; frozen={Frozen}.",
                accountId,
                _rivalsCache.IsFrozen);
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
            var errorMessage = BuildErrorMessage(ex);
            _persistence.Meta.FailRivals(accountId, errorMessage);
            _syncTracker.Error(accountId, errorMessage);
            return new RivalsComputeOutcome(accountId, RivalsComputeOutcomeCode.Error, WasRecomputed: false, DirtySongCount: 0);
        }
    }

    private DirtySongDecision EvaluateDirtySongs(
        string accountId,
        IReadOnlyList<RivalDirtySongRow> dirtySongs,
        IReadOnlyDictionary<string, IReadOnlyList<PlayerScoreDto>>?
            preparedScoresByInstrument = null,
        CancellationToken ct = default)
    {
        var dirtySongsByInstrument = dirtySongs
            .GroupBy(row => row.Instrument, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlySet<string>)group.Select(row => row.SongId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        var dirtyInstruments = new HashSet<string>(dirtySongsByInstrument.Keys, StringComparer.OrdinalIgnoreCase);
        var selectionState = _calculator.ComputeSelectionState(
            accountId,
            dirtyInstruments,
            dirtySongsByInstrument,
            preparedScoresByInstrument,
            ct);
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

    private static string BuildErrorMessage(Exception ex)
    {
        var messages = new List<string>();
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message) &&
                !messages.Contains(current.Message, StringComparer.Ordinal))
            {
                messages.Add(current.Message);
            }
        }

        return messages.Count == 0 ? ex.GetType().Name : string.Join(" | ", messages);
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
