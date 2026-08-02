namespace FSTService.Persistence;

public sealed record ScoreHistoryDedupMaintenanceCommand(
    bool Execute,
    string? ExpectedDigest)
{
    public const string MaintenanceFlag = "--score-history-dedup-maintenance";
    public const string ExecuteFlag = "--score-history-dedup-execute";
    public const string ExpectedDigestFlag =
        "--expected-score-history-dedup-digest";

    public static ScoreHistoryDedupMaintenanceCommand? Parse(
        IReadOnlyList<string> args)
    {
        var maintenanceCount = Count(args, MaintenanceFlag);
        var executeCount = Count(args, ExecuteFlag);
        var digestIndexes = args
            .Select((argument, index) => (argument, index))
            .Where(item => item.argument.Equals(
                ExpectedDigestFlag,
                StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .ToArray();

        if (maintenanceCount == 0 && executeCount == 0 && digestIndexes.Length == 0)
            return null;

        if (maintenanceCount != 1)
        {
            throw new ArgumentException(
                $"{MaintenanceFlag} must be specified exactly once.");
        }

        if (executeCount > 1)
        {
            throw new ArgumentException(
                $"{ExecuteFlag} may be specified only once.");
        }

        if (digestIndexes.Length > 1)
        {
            throw new ArgumentException(
                $"{ExpectedDigestFlag} may be specified only once.");
        }

        string? expectedDigest = null;
        if (digestIndexes.Length == 1)
        {
            var index = digestIndexes[0];
            if (index + 1 >= args.Count)
            {
                throw new ArgumentException(
                    $"{ExpectedDigestFlag} requires a SHA-256 digest.");
            }

            expectedDigest = NormalizeDigest(args[index + 1]);
        }

        var execute = executeCount == 1;
        if (execute && expectedDigest is null)
        {
            throw new ArgumentException(
                $"{ExecuteFlag} requires {ExpectedDigestFlag}.");
        }

        if (!execute && expectedDigest is not null)
        {
            throw new ArgumentException(
                $"{ExpectedDigestFlag} is valid only with {ExecuteFlag}.");
        }

        return new ScoreHistoryDedupMaintenanceCommand(execute, expectedDigest);
    }

    internal static string NormalizeDigest(string digest)
    {
        var normalized = digest?.Trim().ToLowerInvariant()
            ?? throw new ArgumentNullException(nameof(digest));
        if (normalized.Length != 64
            || normalized.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Expected score-history dedup digest must be exactly " +
                "64 hexadecimal characters.",
                nameof(digest));
        }

        return normalized;
    }

    private static int Count(IReadOnlyList<string> args, string value)
        => args.Count(argument => argument.Equals(
            value,
            StringComparison.OrdinalIgnoreCase));
}
