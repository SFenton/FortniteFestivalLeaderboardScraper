namespace FSTService.Scraping;

public sealed record LeaderboardRivalsRecomputeCommand(string AccountId)
{
    public const string AccountFlag =
        "--leaderboard-rivals-recompute-account";

    public static LeaderboardRivalsRecomputeCommand? Parse(
        IReadOnlyList<string> args)
    {
        var matches = args
            .Select((argument, index) => (argument, index))
            .Where(item =>
                item.argument.Equals(
                    AccountFlag,
                    StringComparison.OrdinalIgnoreCase)
                || item.argument.StartsWith(
                    $"{AccountFlag}=",
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0)
            return null;
        if (matches.Length != 1)
        {
            throw new ArgumentException(
                $"{AccountFlag} must be specified exactly once.");
        }

        var (argument, index) = matches[0];
        var equalsIndex = argument.IndexOf('=');
        var accountId = equalsIndex >= 0
            ? argument[(equalsIndex + 1)..]
            : index + 1 < args.Count
                ? args[index + 1]
                : string.Empty;
        accountId = accountId.Trim();
        if (string.IsNullOrWhiteSpace(accountId)
            || accountId.StartsWith("-", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{AccountFlag} requires an account id.");
        }

        return new LeaderboardRivalsRecomputeCommand(accountId);
    }
}

public sealed record LeaderboardRivalsRecomputeReport(
    string AccountId,
    int RivalRows,
    int SampleRows,
    int CacheEntries);
