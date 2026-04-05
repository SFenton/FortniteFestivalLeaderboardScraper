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
}
