using System.Net.Http.Headers;
using System.Text.Json;
using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// One-time per user: walks seasonal leaderboards backwards to reconstruct
/// the timeline of when a user set each high score. Produces <c>ScoreHistory</c>
/// entries that capture the progression (first play → each improvement).
///
/// <para>Algorithm per song/instrument:</para>
/// <list type="number">
///   <item>Read the all-time entry (from instrument DB). Get season S = current high-score season.</item>
///   <item>Query seasons F…S (where F = song's <c>FirstSeenSeason</c>) via
///         <see cref="GlobalLeaderboardScraper.LookupSeasonalAsync"/>. Seasons before the
///         song existed are skipped, significantly reducing API calls for newer songs.</item>
///   <item>Collect all season responses into a list sorted by endTime ascending.</item>
///   <item>Walk the sorted list, keeping only entries where the score strictly increases
///         (the moments the player actually improved).</item>
///   <item>Insert each kept entry as a <c>ScoreHistory</c> row with point-in-time snapshot data.</item>
/// </list>
///
/// Designed to be interruptible and resumable: progress is persisted per
/// (account, song, instrument) in <c>HistoryReconProgress</c>.
/// </summary>
public class HistoryReconstructor
{
    private readonly GlobalLeaderboardScraper _scraper;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly MetaDatabase _metaDb;
    private readonly HttpClient _http;
    private readonly ScrapeProgressTracker _progress;
    private readonly ILogger<HistoryReconstructor> _log;

    public HistoryReconstructor(
        GlobalLeaderboardScraper scraper,
        GlobalLeaderboardPersistence persistence,
        HttpClient http,
        ScrapeProgressTracker progress,
        ILogger<HistoryReconstructor> log)
    {
        _scraper = scraper;
        _persistence = persistence;
        _metaDb = persistence.Meta;
        _http = http;
        _progress = progress;
        _log = log;
    }

    // ─── Season Window Discovery ────────────────────────────────

    /// <summary>
    /// Discover season windows from the Epic events API and cache them in the DB.
    /// If already cached, returns the cached list. If the API is unavailable,
    /// falls back to probing by convention (season_1, season_2, …).
    /// </summary>
    public virtual async Task<IReadOnlyList<SeasonWindowInfo>> DiscoverSeasonWindowsAsync(
        string accessToken,
        string callerAccountId,
        CancellationToken ct = default)
    {
        // Check cache first
        var cached = _metaDb.GetSeasonWindows();
        if (cached.Count > 0)
        {
            _log.LogDebug("Using {Count} cached season windows.", cached.Count);
            return cached;
        }

        // Try the events API
        _log.LogInformation("Discovering season windows from events API...");

        try
        {
            var windows = await FetchSeasonWindowsFromApiAsync(accessToken, callerAccountId, ct);
            if (windows.Count > 0)
            {
                foreach (var w in windows)
                    _metaDb.UpsertSeasonWindow(w.SeasonNumber, w.EventId, w.WindowId);

                _log.LogInformation("Discovered and cached {Count} season windows from events API.", windows.Count);
                return windows;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Events API call failed. Falling back to convention-based probing.");
        }

        // Fallback: probe by convention
        var probed = await ProbeSeasonWindowsAsync(accessToken, callerAccountId, ct);
        foreach (var w in probed)
            _metaDb.UpsertSeasonWindow(w.SeasonNumber, w.EventId, w.WindowId);

        _log.LogInformation("Discovered and cached {Count} season windows via probing.", probed.Count);
        return probed;
    }

    /// <summary>
    /// Fetch season windows from the events API by parsing the response for window IDs.
    /// </summary>
    private async Task<List<SeasonWindowInfo>> FetchSeasonWindowsFromApiAsync(
        string accessToken, string callerAccountId, CancellationToken ct)
    {
        var url = $"https://events-public-service-live.ol.epicgames.com" +
                  $"/api/v1/events/FNFestival/data/{callerAccountId}?showPastEvents=true";

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            _log.LogWarning("Events API returned {Status}.", res.StatusCode);
            return [];
        }

        var json = await res.Content.ReadAsStringAsync(ct);
        return ParseSeasonWindowsFromEventsJson(json);
    }

    /// <summary>
    /// Parse the events API JSON to extract seasonal leaderboard window IDs.
    /// Looks for window IDs matching patterns like "season_N" or containing
    /// seasonal identifiers in the FNFestival event data.
    /// </summary>
    internal static List<SeasonWindowInfo> ParseSeasonWindowsFromEventsJson(string json)
    {
        var windows = new List<SeasonWindowInfo>();

        try
        {
            using var doc = JsonDocument.Parse(json);

            // The events API returns data with "events" array containing event
            // definitions, each with "eventWindows" that include window IDs.
            if (doc.RootElement.TryGetProperty("events", out var events))
            {
                foreach (var evt in events.EnumerateArray())
                {
                    if (!evt.TryGetProperty("eventWindows", out var eventWindows))
                        continue;

                    var eventId = evt.TryGetProperty("eventId", out var eidProp) ? eidProp.GetString() : null;

                    foreach (var window in eventWindows.EnumerateArray())
                    {
                        var windowId = window.TryGetProperty("eventWindowId", out var widProp)
                            ? widProp.GetString() : null;
                        if (windowId is null) continue;

                        // Try to extract season number from window ID
                        // Common patterns: "season_1", "Season1", "s1", etc.
                        var seasonNum = ExtractSeasonNumber(windowId);
                        if (seasonNum > 0)
                        {
                            windows.Add(new SeasonWindowInfo
                            {
                                SeasonNumber = seasonNum,
                                EventId = eventId ?? "",
                                WindowId = windowId,
                            });
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Malformed JSON — return empty
        }

        // Deduplicate by season number (keep first occurrence)
        return windows
            .GroupBy(w => w.SeasonNumber)
            .Select(g => g.First())
            .OrderBy(w => w.SeasonNumber)
            .ToList();
    }

    /// <summary>
    /// Extract a season number from a window ID string.
    /// Handles patterns: "season_1", "Season1", "s1", "S_1", etc.
    /// </summary>
    internal static int ExtractSeasonNumber(string windowId)
    {
        // Try "season_N" or "Season_N"
        var match = System.Text.RegularExpressions.Regex.Match(
            windowId, @"[Ss]eason[_\s]?(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var n))
            return n;

        // Try "s_N" or "S_N" as fallback
        match = System.Text.RegularExpressions.Regex.Match(
            windowId, @"\bs[_\s]?(\d+)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out n))
            return n;

        return 0;
    }

    /// <summary>
    /// Build the event ID prefix for a given season number per FNLookup convention:
    /// Season 1 = "evergreen", Season 2–9 = "season002"–"season009", Season 10+ = "season010" etc.
    /// </summary>
    internal static string GetSeasonPrefix(int seasonNumber)
    {
        if (seasonNumber == 1) return "evergreen";
        return $"season{seasonNumber:D3}";
    }

    /// <summary>
    /// Probe for season windows by convention using FNLookup event ID format:
    /// Season 1 = "evergreen", Season 2+ = "season00N".
    /// Uses a known charted song and a known instrument to probe.
    /// Stops after two consecutive failures.
    /// </summary>
    private async Task<List<SeasonWindowInfo>> ProbeSeasonWindowsAsync(
        string accessToken, string callerAccountId, CancellationToken ct)
    {
        var windows = new List<SeasonWindowInfo>();

        var testSongId = FindProbeSongId();
        if (testSongId is null)
        {
            _log.LogWarning("No songs available for season probing. Stopping discovery.");
            return windows;
        }

        // Try season numbers 1..20 — stop on two consecutive failures
        int consecutiveFailures = 0;
        for (int season = 1; season <= 20 && consecutiveFailures < 2; season++)
        {
            ct.ThrowIfCancellationRequested();

            // FNLookup convention:
            //   Season 1:  eventId = evergreen_{su},        windowId = {su}_{type}
            //   Season N:  eventId = season00N_{su},         windowId = {su}_{type}
            //   Alltime:   eventId = alltime_{su}_{type},    windowId = alltime
            // Our LookupSeasonalAsync builds: eventId = {prefix}_{songId}, windowId = {songId}_{instrument}
            // So we pass the season prefix as the "windowId" parameter.
            var seasonPrefix = GetSeasonPrefix(season);
            try
            {
                var entry = await _scraper.LookupSeasonalAsync(
                    testSongId, "Solo_Guitar", seasonPrefix,
                    callerAccountId, accessToken, callerAccountId, ct: ct);

                // Even if entry is null (the caller has no score in that season),
                // a non-error response means the season window exists.
                windows.Add(new SeasonWindowInfo
                {
                    SeasonNumber = season,
                    EventId = $"{seasonPrefix}_{testSongId}",
                    WindowId = seasonPrefix,
                });
                consecutiveFailures = 0;

                _log.LogDebug("Season {Season} window '{Prefix}' exists.", season, seasonPrefix);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                consecutiveFailures++;
                _log.LogDebug("Season {Season} probe failed (attempt {Failures}/2): {Message}",
                    season, consecutiveFailures, ex.Message);
            }
        }

        return windows;
    }

    /// <summary>Find a song ID from the instrument DBs that's known to have entries.</summary>
    private string? FindProbeSongId()
    {
        foreach (var instrument in GlobalLeaderboardScraper.AllInstruments)
        {
            var db = _persistence.GetOrCreateInstrumentDb(instrument);
            var count = db.GetTotalEntryCount();
            if (count > 0)
            {
                // Get any song ID from this DB
                return db.GetAnySongId();
            }
        }
        return null;
    }

    // ─── Reconstruction ─────────────────────────────────────────

    /// <summary>
    /// Reconstruct score history for a single registered user.
    /// This is a one-time operation per user — once complete, the history
    /// is marked as "reconstructed" and won't be re-run.
    /// </summary>
    /// <returns>Total <c>ScoreHistory</c> entries created.</returns>
    public virtual async Task<int> ReconstructAccountAsync(
        string accountId,
        IReadOnlyList<SeasonWindowInfo> seasonWindows,
        string accessToken,
        string callerAccountId,
        int degreeOfParallelism = 16,
        AdaptiveConcurrencyLimiter? sharedLimiter = null,
        CancellationToken ct = default)
    {
        // Check if already completed
        var status = _metaDb.GetHistoryReconStatus(accountId);
        if (status?.Status == "complete")
        {
            _log.LogDebug("History reconstruction already complete for {AccountId}.", accountId);
            return 0;
        }

        // Gather all the user's all-time entries across all instruments
        var allEntries = new List<(string SongId, string Instrument, LeaderboardEntry Entry)>();
        foreach (var instrument in GlobalLeaderboardScraper.AllInstruments)
        {
            var instrumentDb = _persistence.GetOrCreateInstrumentDb(instrument);
            var scores = instrumentDb.GetPlayerScores(accountId);
            foreach (var score in scores)
            {
                allEntries.Add((score.SongId, instrument, new LeaderboardEntry
                {
                    AccountId = accountId,
                    Score = score.Score,
                    Accuracy = score.Accuracy,
                    IsFullCombo = score.IsFullCombo,
                    Stars = score.Stars,
                    Season = score.Season,
                    Percentile = score.Percentile,
                    EndTime = score.EndTime,
                }));
            }
        }

        // Filter: only entries where Season > 1 need reconstruction.
        // Season 0 or 1 means the first play IS the current high score — no history to find.
        var reconstructable = allEntries.Where(e => e.Entry.Season > 1).ToList();

        if (reconstructable.Count == 0)
        {
            _log.LogInformation("No reconstructable history for {AccountId} (all scores in season 0–1).", accountId);
            _metaDb.EnqueueHistoryRecon(accountId, 0);
            _metaDb.CompleteHistoryRecon(accountId);
            return 0;
        }

        // Set up tracking
        _metaDb.EnqueueHistoryRecon(accountId, reconstructable.Count);
        _metaDb.StartHistoryRecon(accountId);

        // Get already-processed pairs (for resumption)
        var alreadyProcessed = _metaDb.GetProcessedHistoryReconPairs(accountId);

        // Bulk-load FirstSeenSeason data so we can skip seasons before a song existed
        var firstSeenMap = _metaDb.GetAllFirstSeenSeasons();

        _log.LogInformation(
            "Reconstructing history for {AccountId}: {Total} song/instrument pairs to process ({Already} already done).",
            accountId, reconstructable.Count, alreadyProcessed.Count);

        int totalHistoryEntries = 0;
        int songsProcessed = alreadyProcessed.Count;
        int seasonsQueried = 0;

        // Use the shared limiter if provided (multi-user parallelism), otherwise create a local one.
        bool ownsLimiter = sharedLimiter is null;
        AdaptiveConcurrencyLimiter limiter;
        if (ownsLimiter)
        {
            int initialDop = Math.Max(1, degreeOfParallelism / 2);
            int maxDop = degreeOfParallelism * 2;
            limiter = new AdaptiveConcurrencyLimiter(initialDop, minDop: 2, maxDop: maxDop, _log);
            _progress.SetAdaptiveLimiter(limiter);

            _log.LogInformation(
                "Using local adaptive concurrency limiter: initial DOP={InitialDop}, min={MinDop}, max={MaxDop}.",
                initialDop, 2, maxDop);
        }
        else
        {
            limiter = sharedLimiter;
            _log.LogDebug("Using shared adaptive concurrency limiter (DOP={Dop}).", limiter.CurrentDop);
        }

        try
        {

        // Process song/instrument pairs in parallel, throttled by adaptive limiter.
        // Each inner season query acquires/releases the limiter, so concurrency is
        // controlled at the individual API call level (not the song level).
        var tasks = new List<Task>();
        foreach (var (songId, instrument, alltimeEntry) in reconstructable)
        {
            if (alreadyProcessed.Contains((songId, instrument)))
            {
                continue; // Already processed in a previous run
            }

            // Determine the earliest season this song could have data in.
            // If FirstSeenSeason data is available, use it; otherwise skip entirely —
            // no FirstSeenSeason means the song is likely not released yet (just in the API catalog).
            if (!firstSeenMap.TryGetValue(songId, out var fss))
            {
                _log.LogDebug("Skipping {Song}/{Instrument}: no FirstSeenSeason data (song may not be released yet).",
                    songId, instrument);
                Interlocked.Increment(ref songsProcessed);
                _metaDb.MarkHistoryReconSongProcessed(accountId, songId, instrument);
                continue;
            }
            int songMinSeason = fss.FirstSeenSeason ?? fss.EstimatedSeason;

            tasks.Add(ProcessOneSongAsync(songId, instrument, alltimeEntry, songMinSeason));
        }

        async Task ProcessOneSongAsync(string songId, string instrument, LeaderboardEntry alltimeEntry, int songMinSeason)
        {
            try
            {
                var (entries, queries) = await ReconstructSongHistoryAsync(
                    accountId, songId, instrument, alltimeEntry,
                    songMinSeason, seasonWindows, accessToken, callerAccountId, limiter, ct);

                Interlocked.Add(ref totalHistoryEntries, entries);
                Interlocked.Add(ref seasonsQueried, queries);
                var processed = Interlocked.Increment(ref songsProcessed);

                _metaDb.MarkHistoryReconSongProcessed(accountId, songId, instrument);

                // Update progress every 50 songs
                if (processed % 50 == 0)
                {
                    _metaDb.UpdateHistoryReconProgress(accountId, processed,
                        Volatile.Read(ref seasonsQueried),
                        Volatile.Read(ref totalHistoryEntries));

                    _log.LogDebug(
                        "History recon progress: {Processed}/{Total} songs, DOP={Dop}.",
                        processed, reconstructable.Count, limiter.CurrentDop);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "History recon failed for {AccountId}/{Song}/{Instrument}. Skipping.",
                    accountId, songId, instrument);
                Interlocked.Increment(ref songsProcessed);
            }
        }

        await Task.WhenAll(tasks);

        // Final update
        _metaDb.UpdateHistoryReconProgress(accountId, songsProcessed, seasonsQueried, totalHistoryEntries);
        _metaDb.CompleteHistoryRecon(accountId);

        _log.LogInformation(
            "History reconstruction complete for {AccountId}: {Entries} history entries from {Seasons} seasonal queries across {Songs} songs.",
            accountId, totalHistoryEntries, seasonsQueried, songsProcessed);

        return totalHistoryEntries;
        }
        finally
        {
            if (ownsLimiter)
                limiter.Dispose();
        }
    }

    /// <summary>
    /// Reconstruct the score history for one song/instrument by querying
    /// seasonal leaderboards and building a progression timeline.
    /// Unlike the previous implementation that only kept the best score per season,
    /// this version fetches ALL sessions from each season's <c>sessionHistory</c>
    /// array, giving a complete picture of every score improvement.
    /// </summary>
    /// <param name="firstSeenSeason">The earliest season this song existed in (from FirstSeenSeason data).
    /// Seasons before this are skipped, avoiding unnecessary API calls.</param>
    /// <returns>A tuple of (history entries created, season queries made).</returns>
    private async Task<(int EntriesCreated, int QueriesMade)> ReconstructSongHistoryAsync(
        string accountId,
        string songId,
        string instrument,
        LeaderboardEntry alltimeEntry,
        int firstSeenSeason,
        IReadOnlyList<SeasonWindowInfo> seasonWindows,
        string accessToken,
        string callerAccountId,
        AdaptiveConcurrencyLimiter limiter,
        CancellationToken ct)
    {
        int maxSeason = alltimeEntry.Season;
        if (maxSeason <= 1)
            return (0, 0); // No history to reconstruct

        // Start from the song's FirstSeenSeason instead of season 1.
        // This avoids querying seasons that predate the song's existence.
        int startSeason = Math.Max(1, Math.Min(firstSeenSeason, maxSeason));

        // Build the list of seasons to query
        var seasonsToQuery = new List<(int Season, SeasonWindowInfo Window)>();
        for (int s = startSeason; s <= maxSeason; s++)
        {
            var window = seasonWindows.FirstOrDefault(w => w.SeasonNumber == s);
            if (window is null)
            {
                _log.LogDebug("No window found for season {Season}. Skipping.", s);
                continue;
            }
            seasonsToQuery.Add((s, window));
        }

        if (seasonsToQuery.Count == 0)
            return (0, 0);

        // Query all seasons in parallel, collecting ALL sessions from each.
        // Each season query acquires/releases the adaptive concurrency limiter.
        var allSessions = new System.Collections.Concurrent.ConcurrentBag<(int Season, SessionHistoryEntry Session)>();
        int queriesMade = 0;

        var tasks = seasonsToQuery.Select(async item =>
        {
            var (s, window) = item;
            await limiter.WaitAsync(ct);
            try
            {
                var sessions = await _scraper.LookupSeasonalSessionsAsync(
                    songId, instrument, window.WindowId,
                    accountId, accessToken, callerAccountId, limiter, ct);
                Interlocked.Increment(ref queriesMade);

                if (sessions is not null)
                {
                    foreach (var session in sessions)
                    {
                        allSessions.Add((s, session));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex, "Seasonal lookup failed for {Song}/{Instrument}/season_{Season}.",
                    songId, instrument, s);
                Interlocked.Increment(ref queriesMade);
            }
            finally
            {
                limiter.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);

        if (allSessions.IsEmpty)
            return (0, queriesMade);

        // Sort all sessions by endTime ascending (fall back to season number if endTime is null)
        var sortedSessions = allSessions.ToList();
        sortedSessions.Sort((a, b) =>
        {
            if (a.Session.EndTime is not null && b.Session.EndTime is not null)
            {
                return string.Compare(a.Session.EndTime, b.Session.EndTime, StringComparison.Ordinal);
            }
            return a.Season.CompareTo(b.Season);
        });

        // Walk through sorted sessions, keeping only those where score strictly increases
        var progression = new List<(int Season, SessionHistoryEntry Session)>();
        int prevScore = 0;

        foreach (var (season, session) in sortedSessions)
        {
            if (session.Score > prevScore)
            {
                progression.Add((season, session));
                prevScore = session.Score;
            }
        }

        // Build ScoreHistory entries
        int entriesCreated = 0;
        int? previousScore = null;
        int? previousRank = null;

        foreach (var (season, session) in progression)
        {
            _metaDb.InsertScoreChange(
                songId, instrument, accountId,
                previousScore, session.Score,
                previousRank, session.Rank,
                session.Accuracy, session.IsFullCombo, session.Stars,
                session.Percentile, season, session.EndTime,
                seasonRank: session.Rank);

            previousScore = session.Score;
            previousRank = session.Rank;
            entriesCreated++;
        }

        return (entriesCreated, queriesMade);
    }
}
