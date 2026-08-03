namespace FSTService.Persistence;

public enum PathRepairMaintenanceAction
{
    StageExactFour,
    AlignRankings,
    PromoteExactFour,
    RebuildRankings,
}

public sealed record PathRepairMaintenanceCommand(
    PathRepairMaintenanceAction Action,
    string? ManifestPath,
    string? ManifestOutputPath,
    string? RollbackOutputPath,
    long? ExpectedPublishedScrapeId)
{
    public const string StageFlag = "--path-repair-stage-exact-four";
    public const string AlignRankingsFlag = "--path-repair-align-rankings";
    public const string PromoteFlag = "--path-repair-promote-exact-four";
    public const string RebuildRankingsFlag = "--path-repair-rebuild-rankings";
    public const string ManifestFlag = "--path-repair-manifest";
    public const string ManifestOutputFlag = "--path-repair-manifest-output";
    public const string RollbackOutputFlag = "--path-repair-rollback-output";
    public const string PublishedScrapeIdFlag = "--published-scrape-id";

    public static PathRepairMaintenanceCommand? Parse(
        IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var stageCount = Count(args, StageFlag);
        var alignCount = Count(args, AlignRankingsFlag);
        var promoteCount = Count(args, PromoteFlag);
        var rebuildCount = Count(args, RebuildRankingsFlag);
        var manifestCount = Count(args, ManifestFlag);
        var manifestOutputCount = Count(args, ManifestOutputFlag);
        var rollbackOutputCount = Count(args, RollbackOutputFlag);
        var pathRepairArgumentCount =
            stageCount +
            alignCount +
            promoteCount +
            rebuildCount +
            manifestCount +
            manifestOutputCount +
            rollbackOutputCount;
        if (pathRepairArgumentCount == 0)
            return null;

        if (stageCount + alignCount + promoteCount + rebuildCount != 1)
        {
            throw new ArgumentException(
                $"Specify exactly one of {StageFlag}, {AlignRankingsFlag}, {PromoteFlag}, or {RebuildRankingsFlag}.");
        }

        ValidateSingle(stageCount, StageFlag);
        ValidateSingle(alignCount, AlignRankingsFlag);
        ValidateSingle(promoteCount, PromoteFlag);
        ValidateSingle(rebuildCount, RebuildRankingsFlag);
        ValidateSingle(manifestCount, ManifestFlag);
        ValidateSingle(manifestOutputCount, ManifestOutputFlag);
        ValidateSingle(rollbackOutputCount, RollbackOutputFlag);

        var manifestPath = ReadValue(args, ManifestFlag, manifestCount);
        var manifestOutputPath = ReadValue(
            args,
            ManifestOutputFlag,
            manifestOutputCount);
        var rollbackOutputPath = ReadValue(
            args,
            RollbackOutputFlag,
            rollbackOutputCount);
        var publishedScrapeId = ReadPublishedScrapeId(args);

        if (stageCount == 1)
        {
            Require(manifestOutputPath, ManifestOutputFlag);
            Reject(manifestPath, ManifestFlag, StageFlag);
            Reject(rollbackOutputPath, RollbackOutputFlag, StageFlag);
            if (publishedScrapeId.HasValue)
            {
                throw new ArgumentException(
                    $"{PublishedScrapeIdFlag} is not valid with {StageFlag}.");
            }

            return new PathRepairMaintenanceCommand(
                PathRepairMaintenanceAction.StageExactFour,
                null,
                manifestOutputPath,
                null,
                null);
        }

        if (!publishedScrapeId.HasValue)
        {
            throw new ArgumentException(
                $"{PublishedScrapeIdFlag} is required for path-repair ranking alignment, promotion, and ranking rebuild.");
        }
        Reject(manifestOutputPath, ManifestOutputFlag, "this path-repair command");

        if (alignCount == 1)
        {
            Reject(manifestPath, ManifestFlag, AlignRankingsFlag);
            Reject(rollbackOutputPath, RollbackOutputFlag, AlignRankingsFlag);
            return new PathRepairMaintenanceCommand(
                PathRepairMaintenanceAction.AlignRankings,
                null,
                null,
                null,
                publishedScrapeId);
        }

        Require(manifestPath, ManifestFlag);
        if (promoteCount == 1)
        {
            Require(rollbackOutputPath, RollbackOutputFlag);
            return new PathRepairMaintenanceCommand(
                PathRepairMaintenanceAction.PromoteExactFour,
                manifestPath,
                null,
                rollbackOutputPath,
                publishedScrapeId);
        }

        Reject(rollbackOutputPath, RollbackOutputFlag, RebuildRankingsFlag);
        return new PathRepairMaintenanceCommand(
            PathRepairMaintenanceAction.RebuildRankings,
            manifestPath,
            null,
            null,
            publishedScrapeId);
    }

    private static long? ReadPublishedScrapeId(IReadOnlyList<string> args)
    {
        var count = Count(args, PublishedScrapeIdFlag);
        ValidateSingle(count, PublishedScrapeIdFlag);
        if (count == 0)
            return null;

        var value = ReadValue(args, PublishedScrapeIdFlag, count);
        if (!long.TryParse(value, out var scrapeId) || scrapeId <= 0)
        {
            throw new ArgumentException(
                $"{PublishedScrapeIdFlag} requires a positive integer.");
        }

        return scrapeId;
    }

    private static string? ReadValue(
        IReadOnlyList<string> args,
        string flag,
        int count)
    {
        if (count == 0)
            return null;

        var index = args
            .Select((argument, argumentIndex) => (argument, argumentIndex))
            .Single(item => item.argument.Equals(
                flag,
                StringComparison.OrdinalIgnoreCase))
            .argumentIndex;
        if (index + 1 >= args.Count ||
            string.IsNullOrWhiteSpace(args[index + 1]) ||
            args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{flag} requires a value.");
        }

        return args[index + 1];
    }

    private static void ValidateSingle(int count, string flag)
    {
        if (count > 1)
            throw new ArgumentException($"{flag} may be specified only once.");
    }

    private static void Require(string? value, string flag)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{flag} is required.");
    }

    private static void Reject(
        string? value,
        string flag,
        string command)
    {
        if (value is not null)
            throw new ArgumentException($"{flag} is not valid with {command}.");
    }

    private static int Count(IReadOnlyList<string> args, string value)
        => args.Count(argument => argument.Equals(
            value,
            StringComparison.OrdinalIgnoreCase));
}
