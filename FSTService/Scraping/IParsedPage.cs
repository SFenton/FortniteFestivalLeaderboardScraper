namespace FSTService.Scraping;

/// <summary>
/// Common contract for a parsed leaderboard page, shared between solo and band fetchers.
/// Implemented by <see cref="GlobalLeaderboardScraper.ParsedPage"/> and
/// <see cref="GlobalLeaderboardScraper.ParsedBandPage"/>.
/// </summary>
public interface IParsedPage<out TEntry>
{
    int Page { get; }
    int TotalPages { get; }
    IReadOnlyList<TEntry> Entries { get; }
    int EstimatedBytes { get; }
}
