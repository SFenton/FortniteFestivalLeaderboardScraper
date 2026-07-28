using Microsoft.Extensions.Options;

namespace FSTService.Persistence;

public static class ImprovementNotificationStalenessEvaluator
{
    public static ImprovementNotificationStalenessStatus Evaluate(
        ImprovementNotificationPublicationStatus publication,
        ImprovementNotificationOptions options,
        DateTime nowUtc)
    {
        if (!options.Enabled || !publication.PublishedScrapeId.HasValue)
            return ImprovementNotificationStalenessStatus.Healthy(publication.PublishedScrapeId);

        var requiredCompletedAt = new List<DateTime>(2);
        var publishedScrapesBehind = 0;
        if (options.IncludePlayers)
        {
            if (publication.LatestPlayerCompletedAtUtc.HasValue)
                requiredCompletedAt.Add(publication.LatestPlayerCompletedAtUtc.Value);
            publishedScrapesBehind = Math.Max(
                publishedScrapesBehind,
                publication.PlayerPublishedScrapesBehind);
        }

        if (options.IncludeBands)
        {
            if (publication.LatestBandCompletedAtUtc.HasValue)
                requiredCompletedAt.Add(publication.LatestBandCompletedAtUtc.Value);
            publishedScrapesBehind = Math.Max(
                publishedScrapesBehind,
                publication.BandPublishedScrapesBehind);
        }

        DateTime? oldestCompletedAt = requiredCompletedAt.Count == 0
            ? null
            : requiredCompletedAt.Min();
        var ageOrigin = oldestCompletedAt ?? publication.PublishedAtUtc;
        var age = ageOrigin.HasValue
            ? nowUtc - ageOrigin.Value
            : TimeSpan.MaxValue;
        var incompletePublishedScrape = !publication.IsCompleteForPublishedScrape(
            options.IncludePlayers,
            options.IncludeBands);
        var staleByScrape = options.StaleAfterPublishedScrapes > 0
            && publishedScrapesBehind >= options.StaleAfterPublishedScrapes;
        var staleByAge = options.StaleAfterHours > 0
            && age >= TimeSpan.FromHours(options.StaleAfterHours);
        var isStale = incompletePublishedScrape || staleByScrape || staleByAge;

        return new ImprovementNotificationStalenessStatus(
            PublishedScrapeId: publication.PublishedScrapeId,
            IsStale: isStale,
            IncompletePublishedScrape: incompletePublishedScrape,
            PublishedScrapesBehind: publishedScrapesBehind,
            OldestRequiredCompletedAtUtc: oldestCompletedAt,
            Age: age,
            MarkerStatus: publication.MarkerStatus,
            ErrorMessage: publication.ErrorMessage);
    }
}

public sealed record ImprovementNotificationStalenessStatus(
    long? PublishedScrapeId,
    bool IsStale,
    bool IncompletePublishedScrape,
    int PublishedScrapesBehind,
    DateTime? OldestRequiredCompletedAtUtc,
    TimeSpan Age,
    string? MarkerStatus,
    string? ErrorMessage)
{
    public static ImprovementNotificationStalenessStatus Healthy(long? scrapeId) => new(
        scrapeId,
        IsStale: false,
        IncompletePublishedScrape: false,
        PublishedScrapesBehind: 0,
        OldestRequiredCompletedAtUtc: null,
        Age: TimeSpan.Zero,
        MarkerStatus: null,
        ErrorMessage: null);
}

public sealed class ImprovementNotificationStalenessMonitor : BackgroundService
{
    private readonly ImprovementNotificationService _notifications;
    private readonly IOptions<ImprovementNotificationOptions> _options;
    private readonly ILogger<ImprovementNotificationStalenessMonitor> _log;

    public ImprovementNotificationStalenessMonitor(
        ImprovementNotificationService notifications,
        IOptions<ImprovementNotificationOptions> options,
        ILogger<ImprovementNotificationStalenessMonitor> log)
    {
        _notifications = notifications;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _options.Value;
            if (options.Enabled)
            {
                try
                {
                    var publication = _notifications.GetPublicationStatus();
                    var status = ImprovementNotificationStalenessEvaluator.Evaluate(
                        publication,
                        options,
                        DateTime.UtcNow);
                    if (status.IsStale)
                    {
                        _log.LogError(
                            "Improvement notifications are stale: publishedScrape={PublishedScrapeId}, marker={MarkerStatus}, incompletePublishedScrape={IncompletePublishedScrape}, publishedScrapesBehind={PublishedScrapesBehind}, age={Age}, error={Error}.",
                            status.PublishedScrapeId,
                            status.MarkerStatus,
                            status.IncompletePublishedScrape,
                            status.PublishedScrapesBehind,
                            status.Age,
                            status.ErrorMessage);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Failed to evaluate improvement notification staleness.");
                }
            }

            var interval = options.StalenessCheckInterval > TimeSpan.Zero
                ? options.StalenessCheckInterval
                : TimeSpan.FromMinutes(15);
            await Task.Delay(interval, stoppingToken);
        }
    }
}
