using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// Data flowing between scrape phases. Created once per scrape pass
/// and passed to each orchestrator that needs it.
/// </summary>
public sealed class ScrapePassContext
{
    public required long ScrapeId { get; init; }
    public required string AccessToken { get; init; }
    public required string CallerAccountId { get; init; }
    public required HashSet<string> RegisteredIds { get; init; }
    public required GlobalLeaderboardPersistence.PipelineAggregates Aggregates { get; init; }
    public required IReadOnlyList<GlobalLeaderboardScraper.SongScrapeRequest> ScrapeRequests { get; init; }
    public required int DegreeOfParallelism { get; init; }
    public bool EpicReportedOver100Pages { get; init; }
    public bool LeaderboardScrapeCompleted { get; init; } = true;
    public bool RankingsComputedSuccessfully { get; set; }
    public DateTime? RankingsInputCutoffUtc { get; set; }
    public bool SoloCurrentProjectionRefreshedForPublication { get; set; }
    public bool SoloCurrentProjectionScopesSealedForPublication { get; set; }
    public HashSet<SoloCurrentProjectionScopeKey> NotificationProjectionScopes { get; } = [];
    public HashSet<SoloCurrentProjectionScopeKey> RefreshedProjectionScopes { get; } = [];
    public PostScrapeExecutionLedger PostScrapeOutcomes { get; } = new();

    public void AddNotificationProjectionScope(SoloCurrentProjectionScopeKey scope)
    {
        NotificationProjectionScopes.Add(scope);
        RefreshedProjectionScopes.Remove(scope);
    }
}
