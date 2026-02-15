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
///   <item>Query seasons 1…S via <see cref="GlobalLeaderboardScraper.LookupSeasonalAsync"/>.</item>
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
    private readonly ILogger<HistoryReconstructor> _log;

    /// <summary>Max concurrent API lookups during history reconstruction.</summary>
    private const int DegreeOfParallelism = 2;

    public HistoryReconstructor(
        GlobalLeaderboardScraper scraper,
        GlobalLeaderboardPersistence persistence,
        HttpClient http,
        ILogger<HistoryReconstructor> log)
    {
        _scraper = scraper;
        _persistence = persistence;
        _metaDb = persistence.Meta;
        _http = http;
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
    /// Probe for season windows by convention: test "season_1", "season_2", … until we get no result.
    /// Uses a known charted song and a known instrument to probe.
    /// </summary>
    private async Task<List<SeasonWindowInfo>> ProbeSeasonWindowsAsync(
        string accessToken, string callerAccountId, CancellationToken ct)
    {
        var windows = new List<SeasonWindowInfo>();

        // Try season numbers 1..20 — stop on two consecutive failures
        int consecutiveFailures = 0;
        for (int season = 1; season <= 20 && consecutiveFailures < 2; season++)
        {
            ct.ThrowIfCancellationRequested();

            var windowId = $"season_{season}";
            try
            {
                // Probe by calling the leaderboard with this window ID.
                // We don't need a specific song — any valid song will do.
                // Find a song from the DB that we know has entries.
                var testSongId = FindProbeSongId();
                if (testSongId is null)
                {
                    _log.LogWarning("No songs available for season probing. Stopping discovery.");
                    break;
                }

                var entry = await _scraper.LookupSeasonalAsync(
                    testSongId, "Solo_Guitar", windowId,
                    callerAccountId, accessToken, callerAccountId, ct);

                // Even if entry is null (the caller has no score in that season),
                // a non-error response means the season window exists.
                windows.Add(new SeasonWindowInfo
                {
                    SeasonNumber = season,
                    EventId = $"alltime_{testSongId}_Solo_Guitar",
                    WindowId = windowId,
                });
                consecutiveFailures = 0;

                _log.LogDebug("Season {Season} window '{WindowId}' exists.", season, windowId);
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

        _log.LogInformation(
            "Reconstructing history for {AccountId}: {Total} song/instrument pairs to process ({Already} already done).",
            accountId, reconstructable.Count, alreadyProcessed.Count);

        int totalHistoryEntries = 0;
        int songsProcessed = alreadyProcessed.Count;
        int seasonsQueried = 0;

        using var semaphore = new SemaphoreSlim(DegreeOfParallelism);

        // Process sequentially to be gentle on the API
        foreach (var (songId, instrument, alltimeEntry) in reconstructable)
        {
            ct.ThrowIfCancellationRequested();

            if (alreadyProcessed.Contains((songId, instrument)))
            {
                continue; // Already processed in a previous run
            }

            try
            {
                var (entries, queriesMade) = await ReconstructSongHistoryAsync(
                    accountId, songId, instrument, alltimeEntry,
                    seasonWindows, accessToken, callerAccountId, ct);

                totalHistoryEntries += entries;
                seasonsQueried += queriesMade;
                songsProcessed++;

                _metaDb.MarkHistoryReconSongProcessed(accountId, songId, instrument);

                // Update progress every 10 songs
                if (songsProcessed % 10 == 0)
                {
                    _metaDb.UpdateHistoryReconProgress(accountId, songsProcessed, seasonsQueried, totalHistoryEntries);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "History recon failed for {AccountId}/{Song}/{Instrument}. Skipping.",
                    accountId, songId, instrument);
            }
        }

        // Final update
        _metaDb.UpdateHistoryReconProgress(accountId, songsProcessed, seasonsQueried, totalHistoryEntries);
        _metaDb.CompleteHistoryRecon(accountId);

        _log.LogInformation(
            "History reconstruction complete for {AccountId}: {Entries} history entries from {Seasons} seasonal queries across {Songs} songs.",
            accountId, totalHistoryEntries, seasonsQueried, songsProcessed);

        return totalHistoryEntries;
    }

    /// <summary>
    /// Reconstruct the score history for one song/instrument by querying
    /// seasonal leaderboards and building a progression timeline.
    /// </summary>
    /// <returns>A tuple of (history entries created, season queries made).</returns>
    private async Task<(int EntriesCreated, int QueriesMade)> ReconstructSongHistoryAsync(
        string accountId,
        string songId,
        string instrument,
        LeaderboardEntry alltimeEntry,
        IReadOnlyList<SeasonWindowInfo> seasonWindows,
        string accessToken,
        string callerAccountId,
        CancellationToken ct)
    {
        int maxSeason = alltimeEntry.Season;
        if (maxSeason <= 1)
            return (0, 0); // No history to reconstruct

        // Query seasons 1..maxSeason
        var seasonEntries = new List<(int Season, LeaderboardEntry Entry)>();
        int queriesMade = 0;

        for (int s = 1; s <= maxSeason; s++)
        {
            ct.ThrowIfCancellationRequested();

            var window = seasonWindows.FirstOrDefault(w => w.SeasonNumber == s);
            if (window is null)
            {
                _log.LogDebug("No window found for season {Season}. Skipping.", s);
                continue;
            }

            try
            {
                var entry = await _scraper.LookupSeasonalAsync(
                    songId, instrument, window.WindowId,
                    accountId, accessToken, callerAccountId, ct);
                queriesMade++;

                if (entry is not null)
                {
                    seasonEntries.Add((s, entry));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex, "Seasonal lookup failed for {Song}/{Instrument}/season_{Season}.",
                    songId, instrument, s);
                queriesMade++;
            }
        }

        if (seasonEntries.Count == 0)
            return (0, queriesMade);

        // Sort by endTime ascending (fall back to season number if endTime is null)
        seasonEntries.Sort((a, b) =>
        {
            if (a.Entry.EndTime is not null && b.Entry.EndTime is not null)
            {
                return string.Compare(a.Entry.EndTime, b.Entry.EndTime, StringComparison.Ordinal);
            }
            return a.Season.CompareTo(b.Season);
        });

        // Walk through sorted entries, keeping only those where score strictly increases
        var progression = new List<(int Season, LeaderboardEntry Entry)>();
        int prevScore = 0;

        foreach (var (season, entry) in seasonEntries)
        {
            if (entry.Score > prevScore)
            {
                progression.Add((season, entry));
                prevScore = entry.Score;
            }
        }

        // Build ScoreHistory entries
        int entriesCreated = 0;
        int? previousScore = null;
        int? previousRank = null;

        foreach (var (season, entry) in progression)
        {
            _metaDb.InsertScoreChange(
                songId, instrument, accountId,
                previousScore, entry.Score,
                previousRank, entry.Rank,
                entry.Accuracy, entry.IsFullCombo, entry.Stars,
                entry.Percentile, season, entry.EndTime);

            previousScore = entry.Score;
            previousRank = entry.Rank;
            entriesCreated++;
        }

        return (entriesCreated, queriesMade);
    }
}
