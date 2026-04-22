namespace FSTService.Scraping;

/// <summary>
/// Explicit output contract from the core scrape phase.
/// Returned by <see cref="ScrapeOrchestrator.RunAsync"/> and consumed
/// by downstream orchestrators (PostScrapeOrchestrator, BackfillOrchestrator).
/// </summary>
public sealed class ScrapePassResult
{
    public required ScrapePassContext Context { get; init; }
    public required long ScrapeId { get; init; }
    public required int TotalRequests { get; init; }
    public required long TotalBytes { get; init; }
    public required long TotalEntries { get; init; }
    public required int SongsScraped { get; init; }
    public required TimeSpan ScrapeDuration { get; init; }
}
