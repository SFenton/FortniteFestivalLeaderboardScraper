namespace FSTService.Scraping;

public sealed record SoloFamilyRankingBackfillCommand(bool Execute)
{
    public const string MaintenanceFlag =
        "--solo-family-ranking-backfill";
    public const string ExecuteFlag =
        "--solo-family-ranking-backfill-execute";

    public static SoloFamilyRankingBackfillCommand? Parse(
        IReadOnlyList<string> args)
    {
        var maintenanceCount = Count(args, MaintenanceFlag);
        var executeCount = Count(args, ExecuteFlag);

        if (maintenanceCount == 0 && executeCount == 0)
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

        return new SoloFamilyRankingBackfillCommand(
            Execute: executeCount == 1);
    }

    private static int Count(IReadOnlyList<string> args, string value)
        => args.Count(argument => argument.Equals(
            value,
            StringComparison.OrdinalIgnoreCase));
}
