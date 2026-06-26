namespace FSTService.Api;

internal static class LeaderboardResponseRanks
{
    public const string ComputedRankSource = "computed";
    public const string ChoptExactRankSource = "choptExact";
    public const string EpicRankSource = "epic";
    public const string LocalFilteredRankSource = "localFiltered";
    public const string StoredRankSource = "stored";

    public static int Resolve(
        int apiRank,
        int computedRank,
        int storedRank,
        bool preferComputedRank,
        int? exactRemovedAbove = null,
        bool computedRankOverridesApiRank = false)
    {
        if (preferComputedRank && exactRemovedAbove.HasValue)
        {
            if (apiRank > 0)
                return Math.Max(1, apiRank - exactRemovedAbove.Value);
            if (computedRank > 0)
                return computedRank;
        }

        if (computedRankOverridesApiRank && preferComputedRank && computedRank > 0)
            return computedRank;

        if (apiRank > 0)
            return apiRank;

        if (preferComputedRank && computedRank > 0)
            return computedRank;

        return computedRank > 0 ? computedRank : storedRank;
    }

    public static string ResolveSource(
        int apiRank,
        int computedRank,
        int storedRank,
        bool preferComputedRank,
        int? exactRemovedAbove = null,
        bool computedRankOverridesApiRank = false)
    {
        if (preferComputedRank && exactRemovedAbove.HasValue)
            return ChoptExactRankSource;

        if (computedRankOverridesApiRank && preferComputedRank && computedRank > 0)
            return LocalFilteredRankSource;

        if (apiRank > 0)
            return EpicRankSource;

        if (preferComputedRank && computedRank > 0)
            return LocalFilteredRankSource;

        if (computedRank > 0)
            return ComputedRankSource;

        return storedRank > 0 ? StoredRankSource : ComputedRankSource;
    }
}