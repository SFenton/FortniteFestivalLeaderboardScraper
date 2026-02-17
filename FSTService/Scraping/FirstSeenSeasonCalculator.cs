using FortniteFestival.Core.Services;
using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// Determines the first season each song appeared in the Fortnite Festival
/// leaderboard system. Uses observed season data from the instrument DBs and
/// probes the Epic API for earlier seasons when necessary.
///
/// Algorithm per song:
///   1. Find MIN(Season) across all instrument DBs for that song.
///   2. If MIN is season 2, probe "evergreen" (season 1) to see if the song existed.
///   3. If MIN is > 2, probe season (MIN - 1) to see if the song existed.
///   4. If the probe finds a valid window (no HTTP error), the first-seen season is
///      the probed season; otherwise it's the observed MIN.
///   5. Store the result in MetaDatabase.SongFirstSeenSeason.
///
/// Songs whose FirstSeenSeason is already set are skipped (NULL = needs calculation).
/// </summary>
public class FirstSeenSeasonCalculator
{
    private readonly GlobalLeaderboardScraper _scraper;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly MetaDatabase _metaDb;
    private readonly ScrapeProgressTracker _progress;
    private readonly ILogger<FirstSeenSeasonCalculator> _log;

    public FirstSeenSeasonCalculator(
        GlobalLeaderboardScraper scraper,
        GlobalLeaderboardPersistence persistence,
        ScrapeProgressTracker progress,
        ILogger<FirstSeenSeasonCalculator> log)
    {
        _scraper = scraper;
        _persistence = persistence;
        _metaDb = persistence.Meta;
        _progress = progress;
        _log = log;
    }

    /// <summary>
    /// Calculate FirstSeenSeason for all songs that don't have one yet.
    /// </summary>
    /// <returns>Number of songs that had their FirstSeenSeason calculated.</returns>
    public virtual async Task<int> CalculateAsync(
        FestivalService festivalService,
        string accessToken,
        string callerAccountId,
        int degreeOfParallelism = 16,
        CancellationToken ct = default)
    {
        // Get all song IDs from the catalog
        var allSongs = festivalService.Songs
            .Where(s => s.track?.su is not null)
            .Select(s => s.track.su)
            .ToList();

        // Get songs that already have a stored value
        var alreadyCalculated = _metaDb.GetSongsWithFirstSeenSeason();

        // Filter to only songs that need calculation
        var songsToCalculate = allSongs
            .Where(id => !alreadyCalculated.Contains(id))
            .ToList();

        if (songsToCalculate.Count == 0)
        {
            _log.LogDebug("FirstSeenSeason: all {Total} songs already calculated.", allSongs.Count);
            return 0;
        }

        _log.LogInformation(
            "FirstSeenSeason: calculating for {Count} song(s) ({Already} already done, {Total} total).",
            songsToCalculate.Count, alreadyCalculated.Count, allSongs.Count);

        // Get season windows (needed for probing)
        var seasonWindows = _metaDb.GetSeasonWindows();

        // Compute global MAX(Season) — used as EstimatedSeason for songs with no entries
        var globalMaxSeason = _persistence.GetMaxSeasonAcrossInstruments();

        // Phase 1: Resolve MIN(Season) from instrument DBs — pure local I/O, no DOP needed
        var songMinSeasons = new Dictionary<string, int?>(songsToCalculate.Count);
        foreach (var songId in songsToCalculate)
        {
            songMinSeasons[songId] = _persistence.GetMinSeasonAcrossInstruments(songId);
        }

        // Phase 2: For songs that need probing, use DOP
        var needsProbe = songMinSeasons
            .Where(kvp => kvp.Value.HasValue && kvp.Value.Value >= 2)
            .Select(kvp => (SongId: kvp.Key, MinSeason: kvp.Value!.Value))
            .ToList();

        // Songs with no entries or MIN season 1 can be resolved immediately
        int calculated = 0;
        foreach (var songId in songsToCalculate)
        {
            var min = songMinSeasons[songId];
            if (!min.HasValue)
            {
                // No entries at all — store EstimatedSeason = global max, FirstSeenSeason = null
                if (globalMaxSeason.HasValue)
                {
                    _metaDb.UpsertFirstSeenSeason(songId, null, null, globalMaxSeason.Value, "no_entries_estimated");
                    calculated++;
                    _log.LogDebug("FirstSeenSeason: {SongId} has no entries. EstimatedSeason={Max}.",
                        songId, globalMaxSeason.Value);
                }
                continue;
            }
            if (min.Value == 1)
            {
                // Already the earliest possible season
                _metaDb.UpsertFirstSeenSeason(songId, 1, 1, 1, "min_is_1");
                calculated++;
            }
        }

        if (needsProbe.Count == 0)
        {
            _log.LogInformation("FirstSeenSeason: {Calculated} song(s) resolved without probing.", calculated);
            return calculated;
        }

        _log.LogInformation("FirstSeenSeason: probing {Count} song(s) for earlier seasons...", needsProbe.Count);

        // Use SemaphoreSlim for DOP control (lightweight — no adaptive needed for this)
        using var semaphore = new SemaphoreSlim(degreeOfParallelism, degreeOfParallelism);

        var tasks = needsProbe.Select(async item =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var result = await ProbeEarlierSeasonAsync(
                    item.SongId, item.MinSeason,
                    accessToken, callerAccountId, ct);

                _metaDb.UpsertFirstSeenSeason(
                    item.SongId, result.FirstSeenSeason,
                    item.MinSeason, result.FirstSeenSeason, result.ProbeResult);

                return 1;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex, "FirstSeenSeason probe failed for {SongId}. Using observed MIN={Min}.",
                    item.SongId, item.MinSeason);

                // Fall back to observed min
                _metaDb.UpsertFirstSeenSeason(
                    item.SongId, item.MinSeason,
                    item.MinSeason, item.MinSeason, "probe_failed");

                return 1;
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        var results = await Task.WhenAll(tasks);
        calculated += results.Sum();

        _log.LogInformation("FirstSeenSeason: calculated for {Count} song(s) total.", calculated);
        return calculated;
    }

    /// <summary>
    /// Probe the season before the observed minimum to see if the song existed earlier.
    /// </summary>
    private async Task<(int FirstSeenSeason, string ProbeResult)> ProbeEarlierSeasonAsync(
        string songId, int minObservedSeason,
        string accessToken, string callerAccountId,
        CancellationToken ct)
    {
        // Determine which season to probe
        int probeSeasonNumber = minObservedSeason - 1;
        var seasonPrefix = HistoryReconstructor.GetSeasonPrefix(probeSeasonNumber);

        try
        {
            // Use LookupSeasonalAsync to probe the season.
            // If the API returns no_score_found, the window exists (song was present).
            // If the API throws, the window doesn't exist for this song.
            // We use callerAccountId as the target — we don't care about the actual score,
            // just whether the window is valid.
            var entry = await _scraper.LookupSeasonalAsync(
                songId, "Solo_Guitar", seasonPrefix,
                callerAccountId, accessToken, callerAccountId, ct: ct);

            // Window exists — the song was present in the earlier season.
            // entry may be null (no_score_found) or non-null (we had a score), either way it's valid.
            _log.LogDebug("FirstSeenSeason: {SongId} existed in season {Season} (probe={Prefix}).",
                songId, probeSeasonNumber, seasonPrefix);

            return (probeSeasonNumber, $"found_in_{seasonPrefix}");
        }
        catch (HttpRequestException)
        {
            // API error — likely the window doesn't exist for this song in the earlier season.
            // The observed MIN is correct.
            _log.LogDebug("FirstSeenSeason: {SongId} not found in season {Season} (probe={Prefix}). Using MIN={Min}.",
                songId, probeSeasonNumber, seasonPrefix, minObservedSeason);

            return (minObservedSeason, $"not_found_in_{seasonPrefix}");
        }
    }
}
