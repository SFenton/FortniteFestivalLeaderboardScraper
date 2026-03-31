using System.Collections.Concurrent;
using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// A song-parallel batch processing machine that fires all songs concurrently,
/// batching registered users into V2 API calls to perform alltime lookups
/// and seasonal session queries.
///
/// Each instance is <b>discrete and single-use</b>: one caller (post-scrape,
/// registration backfill, etc.) creates a machine, runs it, and discards it.
/// All instances share a <see cref="SharedDopPool"/> that governs total
/// concurrent API calls with priority-aware slot allocation.
/// </summary>
public class SongProcessingMachine
{
    private readonly ILeaderboardQuerier _scraper;
    private readonly BatchResultProcessor _resultProcessor;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly ScrapeProgressTracker _progress;
    private readonly ILogger<SongProcessingMachine> _log;

    public SongProcessingMachine(
        ILeaderboardQuerier scraper,
        BatchResultProcessor resultProcessor,
        GlobalLeaderboardPersistence persistence,
        ScrapeProgressTracker progress,
        ILogger<SongProcessingMachine> log)
    {
        _scraper = scraper;
        _resultProcessor = resultProcessor;
        _persistence = persistence;
        _progress = progress;
        _log = log;
    }

    /// <summary>
    /// Run the machine to completion. Fires all songs in parallel, bounded only
    /// by the shared DOP pool. Each song fans out 6 instruments in parallel,
    /// each instrument performs alltime + seasonal batch lookups.
    /// </summary>
    /// <param name="songIds">All charted song IDs to process.</param>
    /// <param name="users">Users to process (all songs processed for all users).</param>
    /// <param name="seasonWindows">Discovered season windows for seasonal queries.</param>
    /// <param name="accessToken">Epic access token.</param>
    /// <param name="callerAccountId">Caller's Epic account ID.</param>
    /// <param name="pool">Shared DOP pool for slot acquisition.</param>
    /// <param name="isHighPriority">True for post-scrape, false for backfill.</param>
    /// <param name="batchSize">Max accounts per V2 batch call.</param>
    /// <param name="reportProgress">Whether to report to ScrapeProgressTracker (only post-scrape machine should).</param>
    /// <param name="ct">Cancellation token.</param>
    public virtual async Task<MachineResult> RunAsync(
        IReadOnlyList<string> songIds,
        IReadOnlyList<UserWorkItem> users,
        IReadOnlyList<SeasonWindowInfo> seasonWindows,
        string accessToken,
        string callerAccountId,
        SharedDopPool pool,
        bool isHighPriority = true,
        int batchSize = 500,
        bool reportProgress = true,
        CancellationToken ct = default)
    {
        if (songIds.Count == 0 || users.Count == 0)
            return new MachineResult();

        var instruments = GlobalLeaderboardScraper.AllInstruments;

        // Build season prefix lookup
        var seasonPrefixMap = new Dictionary<int, string>();
        foreach (var w in seasonWindows)
            seasonPrefixMap[w.SeasonNumber] = HistoryReconstructor.GetSeasonPrefix(w.SeasonNumber);

        int totalUpdated = 0;
        int totalSessions = 0;
        int totalApiCalls = 0;
        int songsCompleted = 0;

        if (reportProgress)
        {
            _progress.SetAdaptiveLimiter(pool.Limiter);
            _progress.BeginPhaseProgress(songIds.Count);
            _progress.SetPhaseAccounts(users.Count);
        }

        _log.LogInformation(
            "SongProcessingMachine starting: {Songs} songs, {Users} users, batch={BatchSize}, priority={Priority}, DOP={Dop}.",
            songIds.Count, users.Count, batchSize, isHighPriority ? "high" : "low", pool.CurrentDop);

        // ─── Fire ALL songs in parallel ─────────────────────────
        var songTasks = songIds.Select(async songId =>
        {
            ct.ThrowIfCancellationRequested();

            var result = await ProcessSongAsync(
                songId, instruments, users, seasonPrefixMap,
                accessToken, callerAccountId, pool, isHighPriority, batchSize, ct);

            Interlocked.Add(ref totalUpdated, result.EntriesUpdated);
            Interlocked.Add(ref totalSessions, result.SessionsInserted);
            Interlocked.Add(ref totalApiCalls, result.ApiCalls);
            Interlocked.Increment(ref songsCompleted);

            if (reportProgress)
                _progress.ReportPhaseItemComplete();
        }).ToList();

        await Task.WhenAll(songTasks);

        if (reportProgress)
            _progress.SetAdaptiveLimiter(null);

        _log.LogInformation(
            "SongProcessingMachine complete: {Updated} entries, {Sessions} sessions, {ApiCalls} API calls across {Songs} songs.",
            totalUpdated, totalSessions, totalApiCalls, songIds.Count);

        return new MachineResult
        {
            EntriesUpdated = totalUpdated,
            SessionsInserted = totalSessions,
            ApiCalls = totalApiCalls,
            UsersProcessed = users.Count,
        };
    }

    /// <summary>
    /// Process one song across all instruments for all users.
    /// Fires 6 instrument tasks in parallel.
    /// </summary>
    private async Task<SongStepResult> ProcessSongAsync(
        string songId,
        IReadOnlyList<string> instruments,
        IReadOnlyList<UserWorkItem> users,
        IReadOnlyDictionary<int, string> seasonPrefixMap,
        string accessToken,
        string callerAccountId,
        SharedDopPool pool,
        bool isHighPriority,
        int batchSize,
        CancellationToken ct)
    {
        int entriesUpdated = 0;
        int sessionsInserted = 0;
        int apiCalls = 0;

        var instrumentTasks = instruments.Select(async instrument =>
        {
            var result = await ProcessSongInstrumentAsync(
                songId, instrument, users, seasonPrefixMap,
                accessToken, callerAccountId, pool, isHighPriority, batchSize, ct);

            Interlocked.Add(ref entriesUpdated, result.EntriesUpdated);
            Interlocked.Add(ref sessionsInserted, result.SessionsInserted);
            Interlocked.Add(ref apiCalls, result.ApiCalls);
        }).ToList();

        await Task.WhenAll(instrumentTasks);

        return new SongStepResult
        {
            EntriesUpdated = entriesUpdated,
            SessionsInserted = sessionsInserted,
            ApiCalls = apiCalls,
        };
    }

    /// <summary>
    /// Process one song + one instrument for all users.
    /// Performs alltime batch lookup, then seasonal session lookups per required season.
    /// </summary>
    private async Task<SongStepResult> ProcessSongInstrumentAsync(
        string songId,
        string instrument,
        IReadOnlyList<UserWorkItem> users,
        IReadOnlyDictionary<int, string> seasonPrefixMap,
        string accessToken,
        string callerAccountId,
        SharedDopPool pool,
        bool isHighPriority,
        int batchSize,
        CancellationToken ct)
    {
        int entriesUpdated = 0;
        int sessionsInserted = 0;
        int apiCalls = 0;

        // ─── Alltime lookups ──────────────────────────────────
        var alltimeUsers = users
            .Where(u => u.AllTimeNeeded && !u.IsAlreadyChecked(songId, instrument))
            .ToList();

        if (alltimeUsers.Count > 0)
        {
            var existingSet = BuildExistingEntrySet(songId, instrument, alltimeUsers);

            for (int offset = 0; offset < alltimeUsers.Count; offset += batchSize)
            {
                var chunk = alltimeUsers.GetRange(offset, Math.Min(batchSize, alltimeUsers.Count - offset));
                var targetIds = chunk.Select(u => u.AccountId).ToList();

                // Acquire DOP slot only for the HTTP call, release immediately after
                List<LeaderboardEntry> entries;
                if (isHighPriority) await pool.AcquireHighAsync(ct);
                else await pool.AcquireLowAsync(ct);
                try
                {
                    _progress.ReportPhaseRequest();
                    Interlocked.Increment(ref apiCalls);

                    try
                    {
                        entries = await _scraper.LookupMultipleAccountsAsync(
                            songId, instrument, targetIds,
                            accessToken, callerAccountId, pool.Limiter, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogDebug(ex, "Alltime batch failed for {Song}/{Instrument}.", songId, instrument);
                        _progress.ReportPhaseRetry();
                        continue;
                    }
                }
                finally
                {
                    if (isHighPriority) pool.ReleaseHigh();
                    else pool.ReleaseLow();
                }

                // DB processing runs outside the DOP slot — no API concurrency needed
                var count = _resultProcessor.ProcessAlltimeResults(songId, instrument, entries, existingSet);
                Interlocked.Add(ref entriesUpdated, count);
                if (count > 0)
                    _progress.ReportPhaseEntryUpdated(count);

                foreach (var user in chunk)
                {
                    if (user.Purposes.HasFlag(WorkPurpose.Backfill))
                    {
                        bool found = entries.Any(e => e.AccountId.Equals(user.AccountId, StringComparison.OrdinalIgnoreCase));
                        _resultProcessor.MarkBackfillChecked(user.AccountId, songId, instrument, found);
                    }
                }
            }
        }

        // ─── Seasonal session lookups ─────────────────────────
        var seasonUserGroups = new Dictionary<int, List<UserWorkItem>>();
        foreach (var user in users)
        {
            if (user.SeasonsNeeded.Count == 0) continue;
            if (user.IsAlreadyChecked(songId, instrument)) continue;

            foreach (var season in user.SeasonsNeeded)
            {
                if (!seasonPrefixMap.ContainsKey(season)) continue;

                if (!seasonUserGroups.TryGetValue(season, out var list))
                {
                    list = [];
                    seasonUserGroups[season] = list;
                }
                list.Add(user);
            }
        }

        foreach (var (season, seasonUsers) in seasonUserGroups)
        {
            var seasonPrefix = seasonPrefixMap[season];

            for (int offset = 0; offset < seasonUsers.Count; offset += batchSize)
            {
                var chunk = seasonUsers.GetRange(offset, Math.Min(batchSize, seasonUsers.Count - offset));
                var targetIds = chunk.Select(u => u.AccountId).ToList();

                // Acquire DOP slot only for the HTTP call, release immediately after
                List<SessionHistoryEntry> sessions;
                if (isHighPriority) await pool.AcquireHighAsync(ct);
                else await pool.AcquireLowAsync(ct);
                try
                {
                    _progress.ReportPhaseRequest();
                    Interlocked.Increment(ref apiCalls);

                    try
                    {
                        sessions = await _scraper.LookupMultipleAccountSessionsAsync(
                            songId, instrument, seasonPrefix, targetIds,
                            accessToken, callerAccountId, pool.Limiter, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogDebug(ex, "Seasonal batch failed for {Song}/{Instrument}/{Season}.",
                            songId, instrument, seasonPrefix);
                        _progress.ReportPhaseRetry();
                        continue;
                    }
                }
                finally
                {
                    if (isHighPriority) pool.ReleaseHigh();
                    else pool.ReleaseLow();
                }

                // DB processing runs outside the DOP slot
                var count = _resultProcessor.ProcessSeasonalSessions(songId, instrument, season, sessions);
                Interlocked.Add(ref sessionsInserted, count);
                if (count > 0)
                    _progress.ReportPhaseEntryUpdated(count);
            }
        }

        // Mark history recon processed for users doing that work
        foreach (var user in users)
        {
            if (user.Purposes.HasFlag(WorkPurpose.HistoryRecon) && !user.IsAlreadyChecked(songId, instrument))
                _resultProcessor.MarkHistoryReconProcessed(user.AccountId, songId, instrument);
        }

        return new SongStepResult
        {
            EntriesUpdated = entriesUpdated,
            SessionsInserted = sessionsInserted,
            ApiCalls = apiCalls,
        };
    }

    /// <summary>
    /// Build a set of account IDs that already have entries in the instrument DB
    /// for the given song/instrument. Used to detect stale vs new entries.
    /// </summary>
    private HashSet<string> BuildExistingEntrySet(
        string songId, string instrument, IReadOnlyList<UserWorkItem> users)
    {
        var instrumentDb = _persistence.GetOrCreateInstrumentDb(instrument);
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var user in users)
        {
            var entry = instrumentDb.GetEntry(songId, user.AccountId);
            if (entry is not null)
                existing.Add(user.AccountId);
        }

        return existing;
    }

    /// <summary>Result of one complete machine run.</summary>
    public sealed class MachineResult
    {
        public int EntriesUpdated { get; init; }
        public int SessionsInserted { get; init; }
        public int ApiCalls { get; init; }
        public int UsersProcessed { get; init; }
    }

    /// <summary>Result of processing one song step.</summary>
    private sealed class SongStepResult
    {
        public int EntriesUpdated { get; init; }
        public int SessionsInserted { get; init; }
        public int ApiCalls { get; init; }
    }
}
