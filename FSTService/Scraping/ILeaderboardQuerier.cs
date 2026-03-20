using FortniteFestival.Core.Scraping;

namespace FSTService.Scraping;

/// <summary>
/// Interface for targeted leaderboard queries — lookup individual accounts
/// on specific songs/instruments. Separated from bulk scrape operations
/// to decouple consumers like <see cref="ScoreBackfiller"/>,
/// <see cref="PostScrapeRefresher"/>, <see cref="HistoryReconstructor"/>,
/// and <see cref="FirstSeenSeasonCalculator"/> from the concrete scraper.
/// </summary>
public interface ILeaderboardQuerier
{
    /// <summary>Fetch a player's alltime entry for one song/instrument (V2 POST).</summary>
    Task<LeaderboardEntry?> LookupAccountAsync(
        string songId, string instrument, string targetAccountId,
        string accessToken, string callerAccountId,
        AdaptiveConcurrencyLimiter? limiter = null, CancellationToken ct = default);

    /// <summary>Fetch a player's entry in a specific seasonal window (V2 POST).</summary>
    Task<LeaderboardEntry?> LookupSeasonalAsync(
        string songId, string instrument, string windowId,
        string targetAccountId, string accessToken, string callerAccountId,
        AdaptiveConcurrencyLimiter? limiter = null, CancellationToken ct = default);

    /// <summary>Fetch all individual sessions (runs) for a player in a seasonal window.</summary>
    Task<List<SessionHistoryEntry>?> LookupSeasonalSessionsAsync(
        string songId, string instrument, string windowId,
        string targetAccountId, string accessToken, string callerAccountId,
        AdaptiveConcurrencyLimiter? limiter = null, CancellationToken ct = default);
}
