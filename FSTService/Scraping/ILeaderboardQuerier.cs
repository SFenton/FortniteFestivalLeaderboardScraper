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

    /// <summary>
    /// Fetch a player's entry plus up to ±neighborRadius rank neighbors.
    /// Uses V2 POST (1 call) to get rank, then V1 GET with rank= (1 call)
    /// to fetch the page. Returns target (Source=backfill) and neighbors (Source=neighbor).
    /// </summary>
    Task<(LeaderboardEntry? Target, List<LeaderboardEntry> Neighbors)> LookupAccountWithNeighborsAsync(
        string songId, string instrument, string targetAccountId,
        string accessToken, string callerAccountId,
        int neighborRadius = 50,
        AdaptiveConcurrencyLimiter? limiter = null, CancellationToken ct = default);

    /// <summary>
    /// Fetch alltime entries for multiple accounts on one song/instrument in a single
    /// batched V2 POST. Pages through the response (25 entries per page) until all
    /// requested accounts are returned or no more results.
    /// </summary>
    Task<List<LeaderboardEntry>> LookupMultipleAccountsAsync(
        string songId, string instrument, IReadOnlyList<string> targetAccountIds,
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

    /// <summary>
    /// Fetch all sessions for multiple accounts on one song/instrument in a seasonal window.
    /// Returns a flat list of sessions across all accounts (each tagged with its AccountId).
    /// Pages through the V2 response (25 entries per page).
    /// </summary>
    Task<List<SessionHistoryEntry>> LookupMultipleAccountSessionsAsync(
        string songId, string instrument, string seasonPrefix,
        IReadOnlyList<string> targetAccountIds,
        string accessToken, string callerAccountId,
        AdaptiveConcurrencyLimiter? limiter = null, CancellationToken ct = default);

    /// <summary>Fetch a specific band team's alltime or seasonal entry via V2 exact-team lookup.</summary>
    Task<BandLeaderboardEntry?> LookupBandAsync(
        string songId, string bandType, IReadOnlyList<string> teamAccountIds,
        string windowId, string accessToken, string callerAccountId,
        AdaptiveConcurrencyLimiter? limiter = null, CancellationToken ct = default);
}
