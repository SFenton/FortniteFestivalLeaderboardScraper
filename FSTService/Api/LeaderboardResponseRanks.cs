namespace FSTService.Api;

internal static class LeaderboardResponseRanks
{
    public static int Resolve(int apiRank, int computedRank, int storedRank, bool preferComputedRank)
    {
        if (preferComputedRank && computedRank > 0)
            return computedRank;

        if (apiRank > 0)
            return apiRank;

        return computedRank > 0 ? computedRank : storedRank;
    }
}