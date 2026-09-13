using FSTService.Persistence;

namespace FSTService.Scraping;

internal static class LeaderboardPaginationPlanner
{
    internal static int InitialPageCount(
        int providerReportedTotalPages,
        int configuredMaximumPages)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(
            providerReportedTotalPages);
        ArgumentOutOfRangeException.ThrowIfNegative(
            configuredMaximumPages);
        return configuredMaximumPages > 0
            ? Math.Min(
                providerReportedTotalPages,
                configuredMaximumPages)
            : providerReportedTotalPages;
    }

    internal static bool TryGetSoloThresholds(
        int? maximumScore,
        double triggerMultiplier,
        double validCutoffMultiplier,
        out int triggerThreshold,
        out int validCutoff)
    {
        triggerThreshold = 0;
        validCutoff = 0;
        if (maximumScore is not > 0)
            return false;

        triggerThreshold = checked(
            (int)(maximumScore.Value * triggerMultiplier));
        validCutoff = checked(
            (int)(maximumScore.Value * validCutoffMultiplier));
        return true;
    }

    internal static bool ShouldDeepScrapeSolo(
        IReadOnlyList<LeaderboardEntry> firstPageEntries,
        int triggerThreshold) =>
        firstPageEntries.Count > 0 &&
        firstPageEntries.Max(static entry => entry.Score) >
        triggerThreshold;

    internal static bool ShouldDeepScrapeSolo(
        int topScore,
        int triggerThreshold) =>
        topScore > triggerThreshold;

    internal static bool NeedsTargetDrivenSoloExtension(
        bool deepScrapeTriggered,
        int capturedPageCount,
        int providerReportedTotalPages,
        int validEntryCount,
        int validEntryTarget) =>
        deepScrapeTriggered &&
        validEntryTarget > 0 &&
        capturedPageCount < providerReportedTotalPages &&
        validEntryCount < validEntryTarget;

    internal static int NextSoloBatchEnd(
        int nextPage,
        int providerReportedTotalPages,
        int configuredBatchPages)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nextPage);
        ArgumentOutOfRangeException.ThrowIfNegative(
            providerReportedTotalPages);
        if (configuredBatchPages <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuredBatchPages));
        }

        return Math.Min(
            checked(nextPage + configuredBatchPages),
            providerReportedTotalPages);
    }

    internal static int LegacySoloExtensionEnd(
        int initialPageCount,
        int providerReportedTotalPages,
        int lastOverThresholdPage,
        int configuredExtraPages)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialPageCount);
        ArgumentOutOfRangeException.ThrowIfNegative(
            providerReportedTotalPages);
        ArgumentOutOfRangeException.ThrowIfNegative(
            lastOverThresholdPage);
        ArgumentOutOfRangeException.ThrowIfNegative(
            configuredExtraPages);

        return Math.Min(
            Math.Max(
                checked(
                    lastOverThresholdPage +
                    configuredExtraPages +
                    1),
                checked(
                    initialPageCount +
                    configuredExtraPages)),
            providerReportedTotalPages);
    }

    internal static bool ShouldFetchBandPage(
        int nextPage,
        int providerReportedTotalPages,
        int configuredMaximumPages,
        int validEntryCount,
        int validEntryTarget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nextPage);
        ArgumentOutOfRangeException.ThrowIfNegative(
            providerReportedTotalPages);
        ArgumentOutOfRangeException.ThrowIfNegative(
            configuredMaximumPages);
        ArgumentOutOfRangeException.ThrowIfNegative(
            validEntryCount);
        ArgumentOutOfRangeException.ThrowIfNegative(validEntryTarget);

        if (nextPage >= providerReportedTotalPages)
            return false;
        if (validEntryTarget > 0)
            return validEntryCount < validEntryTarget;
        return nextPage <
               InitialPageCount(
                   providerReportedTotalPages,
                   configuredMaximumPages);
    }

    internal static bool IsBandEntryWithinValidCutoff<TMember>(
        IReadOnlyList<TMember> members,
        Func<TMember, int> instrumentId,
        Func<TMember, int> score,
        SongMaxScores? maximumScores,
        double validCutoffMultiplier)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(instrumentId);
        ArgumentNullException.ThrowIfNull(score);
        if (maximumScores is null ||
            members.Count == 0)
        {
            return true;
        }

        foreach (var member in members)
        {
            var leaderboardType =
                BandInstrumentMapping.ToLeaderboardType(
                    instrumentId(member));
            if (leaderboardType is null)
                continue;
            var maximum =
                maximumScores.GetByInstrument(
                    leaderboardType);
            if (maximum is > 0 &&
                score(member) >
                checked(
                    (int)(maximum.Value *
                    validCutoffMultiplier)))
            {
                return false;
            }
        }
        return true;
    }
}
