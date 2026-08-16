using System.Collections.Concurrent;

namespace FSTService.Scraping;

public enum PostScrapePhaseCriticality
{
    PublicationCritical,
    BestEffort,
}

public static class PostScrapePhasePolicy
{
    private static readonly IReadOnlyDictionary<string, PostScrapePhaseCriticality> Policies =
        new Dictionary<string, PostScrapePhaseCriticality>(StringComparer.Ordinal)
        {
            ["RankRecompute"] = PostScrapePhaseCriticality.PublicationCritical,
            ["FirstSeenSeason"] = PostScrapePhaseCriticality.BestEffort,
            ["AccountNameResolution"] = PostScrapePhaseCriticality.BestEffort,
            ["RefreshRegisteredUsers"] = PostScrapePhaseCriticality.PublicationCritical,
            ["ActivateShadowSnapshotsEarly"] = PostScrapePhaseCriticality.PublicationCritical,
            ["BandExtraction"] = PostScrapePhaseCriticality.PublicationCritical,
            ["LegacyBandScrape"] = PostScrapePhaseCriticality.PublicationCritical,
            ["RegisteredPlayerBandDiscovery"] = PostScrapePhaseCriticality.BestEffort,
            ["RegisteredBandTargetedProcessing"] = PostScrapePhaseCriticality.BestEffort,
            ["BandMaintenance"] = PostScrapePhaseCriticality.PublicationCritical,
            ["PrepareSoloCurrentProjectionForDerived"] = PostScrapePhaseCriticality.PublicationCritical,
            ["ComputeRankings"] = PostScrapePhaseCriticality.PublicationCritical,
            ["Rivals"] = PostScrapePhaseCriticality.PublicationCritical,
            ["LeaderboardRivals"] = PostScrapePhaseCriticality.PublicationCritical,
            ["PlayerStatsTiers"] = PostScrapePhaseCriticality.PublicationCritical,
            ["ActivateShadowSnapshots"] = PostScrapePhaseCriticality.PublicationCritical,
            ["SealSoloCurrentProjectionScopes"] = PostScrapePhaseCriticality.PublicationCritical,
            ["Cleanup.SoloCurrentProjection"] = PostScrapePhaseCriticality.PublicationCritical,
            ["Cleanup.PrecomputeAll"] = PostScrapePhaseCriticality.PublicationCritical,
            ["ImprovementNotifications"] = PostScrapePhaseCriticality.BestEffort,
            ["Cleanup.SoloExcessEntries"] = PostScrapePhaseCriticality.BestEffort,
            ["Cleanup.RankHistoryRetention"] = PostScrapePhaseCriticality.BestEffort,
            ["Cleanup.BandRankHistoryRetention"] = PostScrapePhaseCriticality.BestEffort,
            ["Cleanup.ServiceLevelRetention"] = PostScrapePhaseCriticality.BestEffort,
        };

    public static IReadOnlyDictionary<string, PostScrapePhaseCriticality> All => Policies;

    public static PostScrapePhaseCriticality GetCriticality(string phase)
    {
        if (!Policies.TryGetValue(phase, out var criticality))
            throw new InvalidOperationException(
                $"Post-scrape phase '{phase}' has no explicit publication criticality.");
        return criticality;
    }

    public static bool IsSuccessfulStatus(
        string status,
        PostScrapePhaseCriticality criticality) =>
        string.Equals(status, "completed", StringComparison.Ordinal)
        || (criticality == PostScrapePhaseCriticality.BestEffort
            && string.Equals(status, "skipped", StringComparison.Ordinal));
}

public sealed record PostScrapePhaseOutcome(
    string Phase,
    PostScrapePhaseCriticality Criticality,
    bool Success,
    string? ErrorMessage)
{
    public string Status { get; init; } = Success ? "completed" : "failed";

    public bool IsSuccessfulForPublication =>
        Status == "completed"
            ? Success
            : Status == "skipped"
              && Criticality == PostScrapePhaseCriticality.BestEffort;
}

public sealed class PostScrapeExecutionLedger
{
    private readonly ConcurrentDictionary<string, PostScrapePhaseOutcome> _outcomes =
        new(StringComparer.Ordinal);

    public IReadOnlyCollection<PostScrapePhaseOutcome> Outcomes =>
        _outcomes.Values.OrderBy(static outcome => outcome.Phase, StringComparer.Ordinal).ToArray();

    public IReadOnlyCollection<PostScrapePhaseOutcome> FailedPublicationCriticalPhases =>
        Outcomes.Where(static outcome =>
            !outcome.IsSuccessfulForPublication
            && outcome.Criticality == PostScrapePhaseCriticality.PublicationCritical).ToArray();

    public IReadOnlyCollection<PostScrapePhaseOutcome> FailedBestEffortPhases =>
        Outcomes.Where(static outcome =>
            !outcome.IsSuccessfulForPublication
            && outcome.Criticality == PostScrapePhaseCriticality.BestEffort).ToArray();

    public IReadOnlyCollection<PostScrapePhaseOutcome> InvalidPublicationCriticalSkips =>
        Outcomes.Where(static outcome =>
            outcome.Criticality == PostScrapePhaseCriticality.PublicationCritical
            && outcome.Status == "skipped").ToArray();

    public bool CanPublish => FailedPublicationCriticalPhases.Count == 0;

    public void Record(PostScrapePhaseOutcome outcome) =>
        _outcomes[outcome.Phase] = outcome;
}

public static class ScrapePublicationGuard
{
    public static void EnsureCanPublish(
        long scrapeId,
        PostScrapeExecutionLedger ledger,
        bool enforcePublicationCriticalPhases)
    {
        var invalidSkips = ledger.InvalidPublicationCriticalSkips;
        if (invalidSkips.Count > 0)
        {
            throw new InvalidOperationException(
                $"Scrape {scrapeId} cannot be published because publication-critical " +
                $"phase(s) were invalidly skipped: " +
                $"{string.Join(", ", invalidSkips.Select(static failure => failure.Phase))}.");
        }

        if (!enforcePublicationCriticalPhases || ledger.CanPublish)
            return;

        var failures = ledger.FailedPublicationCriticalPhases;
        throw new InvalidOperationException(
            $"Scrape {scrapeId} cannot be published because publication-critical " +
            $"phase(s) did not complete successfully: " +
            $"{string.Join(", ", failures.Select(static failure => $"{failure.Phase} ({failure.Status})"))}.");
    }
}

public interface IPostScrapePhaseFaultInjector
{
    void BeforePhase(string phase);
}
