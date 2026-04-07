using FortniteFestival.Core.Services;
using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// Determines the first season each song appeared in the Fortnite Festival
/// leaderboard system by probing leaderboards directly via binary search.
///
/// Algorithm per song:
///   1. Get the sorted list of known seasons from season_windows.
///   2. Binary search: probe LookupSeasonalAsync for the mid-point season.
///      - Valid response (200 or no_score_found) → song existed → search earlier.
///      - HttpRequestException (400) → song didn&apos;t exist → search later.
///   3. The lowest valid season is first_seen_season.
///   4. Store with calculation_version = CurrentVersion.
///
/// Songs with calculation_version == CurrentVersion are skipped.
/// Bumping CurrentVersion triggers recalculation of all songs.
/// </summary>
public class FirstSeenSeasonCalculator
{
    /// <summary>
    /// Bump this constant to force recalculation of all songs.
    /// v1 = original MIN(season) + single probe approach.
    /// v2 = binary search across all seasons.
    /// </summary>
    public const int CurrentVersion = 2;

    private readonly ILeaderboardQuerier _scraper;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly IMetaDatabase _metaDb;
    private readonly ScrapeProgressTracker _progress;
    private readonly ILogger<FirstSeenSeasonCalculator> _log;

    public FirstSeenSeasonCalculator(
        ILeaderboardQuerier scraper,
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
    /// Calculate FirstSeenSeason for all songs that need it (missing or outdated version).
    /// </summary>
    /// <returns>Number of songs that had their FirstSeenSeason calculated.</returns>
    public virtual async Task<int> CalculateAsync(
        FestivalService festivalService,
        string accessToken,
        string callerAccountId,
        SharedDopPool pool,
        CancellationToken ct = default)
    {
        // Get all song IDs from the catalog
        var allSongs = festivalService.Songs
            .Where(s => s.track?.su is not null)
            .Select(s => s.track.su)
            .ToList();

        // Get songs already at current version — these are skipped
        var alreadyCurrent = _metaDb.GetSongIdsWithFirstSeenVersion(CurrentVersion);

        // Filter to songs that need (re)calculation
        var songsToCalculate = allSongs
            .Where(id => !alreadyCurrent.Contains(id))
            .ToList();

        if (songsToCalculate.Count == 0)
        {
            _log.LogDebug("FirstSeenSeason: all {Total} songs already at version {Version}.", allSongs.Count, CurrentVersion);
            return 0;
        }

        _log.LogInformation(
            "FirstSeenSeason v{Version}: calculating for {Count} song(s) ({Already} already done, {Total} total).",
            CurrentVersion, songsToCalculate.Count, alreadyCurrent.Count, allSongs.Count);

        _progress.BeginPhaseProgress(songsToCalculate.Count);

        // Get known seasons sorted ascending
        var seasonWindows = _metaDb.GetSeasonWindows();
        var seasons = seasonWindows
            .Select(w => w.SeasonNumber)
            .OrderBy(n => n)
            .ToList();

        if (seasons.Count == 0)
        {
            _log.LogWarning("FirstSeenSeason: no season windows discovered. Cannot calculate.");
            return 0;
        }

        // Also get MIN(season) per song from instrument DBs for diagnostic min_observed_season
        var songMinSeasons = new Dictionary<string, int?>(songsToCalculate.Count);
        foreach (var songId in songsToCalculate)
        {
            songMinSeasons[songId] = _persistence.GetMinSeasonAcrossInstruments(songId);
        }

        _log.LogInformation("FirstSeenSeason: binary searching across {SeasonCount} seasons for {SongCount} song(s)...",
            seasons.Count, songsToCalculate.Count);

        int calculated = 0;

        var tasks = songsToCalculate.Select(async songId =>
        {
            try
            {
                var result = await BinarySearchFirstSeenAsync(
                    songId, seasons, accessToken, callerAccountId, pool, ct);

                var minObserved = songMinSeasons.GetValueOrDefault(songId);

                _metaDb.UpsertFirstSeenSeason(
                    songId, result.FirstSeenSeason, minObserved,
                    result.FirstSeenSeason ?? seasons[^1],
                    result.ProbeResult, CurrentVersion);

                _progress.ReportPhaseItemComplete();
                return 1;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "FirstSeenSeason binary search failed for {SongId}.", songId);

                // Store as unresolved at current version so we don't retry indefinitely
                var minObserved = songMinSeasons.GetValueOrDefault(songId);
                _metaDb.UpsertFirstSeenSeason(
                    songId, minObserved, minObserved,
                    minObserved ?? seasons[^1],
                    "binary_search_failed", CurrentVersion);

                _progress.ReportPhaseItemComplete();
                return 1;
            }
        }).ToList();

        var results = await Task.WhenAll(tasks);
        calculated = results.Sum();

        _log.LogInformation("FirstSeenSeason v{Version}: calculated for {Count} song(s).", CurrentVersion, calculated);
        return calculated;
    }

    /// <summary>
    /// Binary search across known seasons to find the earliest one where the song exists.
    /// A valid API response (200 or no_score_found) means the song existed in that season.
    /// An HttpRequestException (400) means it did not.
    /// </summary>
    internal async Task<(int? FirstSeenSeason, string ProbeResult)> BinarySearchFirstSeenAsync(
        string songId, IReadOnlyList<int> seasons,
        string accessToken, string callerAccountId,
        SharedDopPool pool, CancellationToken ct)
    {
        int lo = 0, hi = seasons.Count - 1;
        int? bestFound = null;
        int probeCount = 0;

        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            int season = seasons[mid];
            bool exists = await ProbeSeasonAsync(songId, season, accessToken, callerAccountId, pool, ct);
            probeCount++;

            if (exists)
            {
                bestFound = season;
                hi = mid - 1; // search earlier
            }
            else
            {
                lo = mid + 1; // search later
            }
        }

        if (bestFound is null)
        {
            _log.LogDebug("FirstSeenSeason: {SongId} not found in any of {Count} seasons after {Probes} probes.",
                songId, seasons.Count, probeCount);
            return (null, $"not_found_in_any_season({probeCount}_probes)");
        }

        var prefix = HistoryReconstructor.GetSeasonPrefix(bestFound.Value);
        _log.LogDebug("FirstSeenSeason: {SongId} first seen in {Prefix} after {Probes} probes.",
            songId, prefix, probeCount);
        return (bestFound.Value, $"found_{prefix}_via_binary_search({probeCount}_probes)");
    }

    /// <summary>
    /// Probe a single season to check if a song's leaderboard exists.
    /// Returns true if the API returns a valid response (song existed), false on HttpRequestException.
    /// </summary>
    private async Task<bool> ProbeSeasonAsync(
        string songId, int seasonNumber,
        string accessToken, string callerAccountId,
        SharedDopPool pool, CancellationToken ct)
    {
        var seasonPrefix = HistoryReconstructor.GetSeasonPrefix(seasonNumber);
        var lowToken = await pool.AcquireLowAsync(ct);

        try
        {
            await _scraper.LookupSeasonalAsync(
                songId, "Solo_Guitar", seasonPrefix,
                callerAccountId, accessToken, callerAccountId, ct: ct);

            pool.ReportSuccess();
            _progress.ReportPhaseRequest();
            return true;
        }
        catch (HttpRequestException)
        {
            pool.ReportFailure();
            _progress.ReportPhaseRequest();
            return false;
        }
        finally
        {
            pool.ReleaseLow(lowToken);
        }
    }
}
