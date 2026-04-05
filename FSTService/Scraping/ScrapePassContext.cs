using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// Data flowing between scrape phases. Created once per scrape pass
/// and passed to each orchestrator that needs it.
/// </summary>
public sealed class ScrapePassContext
{
    public required string AccessToken { get; init; }
    public required string CallerAccountId { get; init; }
    public required HashSet<string> RegisteredIds { get; init; }
    public required GlobalLeaderboardPersistence.PipelineAggregates Aggregates { get; init; }
    public required IReadOnlyList<GlobalLeaderboardScraper.SongScrapeRequest> ScrapeRequests { get; init; }
    public required int DegreeOfParallelism { get; init; }

    /// <summary>
    /// When full crawl is enabled, maps (songId, instrument) → set of registered
    /// account IDs that were found with an alltime score during the V1 crawl.
    /// Used downstream to gate V2 seasonal queries: only users with a known
    /// alltime score are queried for seasonal sessions.
    /// Null when full crawl is disabled (legacy mode).
    /// </summary>
    public Dictionary<(string SongId, string Instrument), HashSet<string>>? FoundScoresBySongInstrument { get; init; }
}
