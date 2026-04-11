namespace FSTService.Scraping;

/// <summary>
/// Solo (per-instrument) page fetcher.  Inherits DOP gating, rate limiting,
/// CDN resilience, and retry logic from <see cref="PageFetcherBase{TEntry}"/>.
/// Provides only the solo-specific URL pattern and parser.
/// </summary>
public sealed class SoloPageFetcher : PageFetcherBase<LeaderboardEntry>
{
    private const string EventsBase = "https://events-public-service-live.ol.epicgames.com";

    public SoloPageFetcher(
        ResilientHttpExecutor executor,
        SharedDopPool pool,
        ScrapeProgressTracker progress,
        ILogger log)
        : base(executor, pool, progress, log)
    {
    }

    protected override string BuildUrl(string songId, string type, int page, string accountId) =>
        $"{EventsBase}/api/v1/leaderboards/FNFestival/alltime_{songId}_{type}" +
        $"/alltime/{accountId}?page={page}&rank=0&appId=Fortnite&showLiveSessions=false";

    protected override async Task<IParsedPage<LeaderboardEntry>?> ParseResponseAsync(Stream stream, CancellationToken ct) =>
        await GlobalLeaderboardScraper.ParsePageAsync(stream, ct);

    protected override void ProcessEntries(string songId, string type, IParsedPage<LeaderboardEntry> page)
    {
        // Solo entry processing is handled by the caller's onSongComplete callback,
        // not inside the fetcher.  This override is intentionally a no-op.
    }
}
