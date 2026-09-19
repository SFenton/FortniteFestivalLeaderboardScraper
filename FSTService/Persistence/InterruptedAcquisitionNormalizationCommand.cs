using System.Globalization;

namespace FSTService.Persistence;

public sealed record InterruptedAcquisitionNormalizationCommand(
    bool Execute,
    bool CheckOnly,
    long ScrapeId,
    long ExpectedPublishedScrapeId,
    long ExpectedCurrentPublicationId,
    long ExpectedPreviousPublicationId,
    long ExpectedWorkingPublicationId,
    string ExpectedWorkerInstanceId,
    DateTime ExpectedWorkerFreshnessUtc,
    string ExpectedPhaseId,
    int ExpectedAttempt)
{
    public const string MaintenanceFlag =
        "--interrupted-acquisition-normalization";
    public const string ExecuteFlag =
        "--interrupted-acquisition-normalization-execute";
    public const string CheckFlag =
        "--interrupted-acquisition-normalization-check";
    public const string ScrapeIdFlag =
        "--interrupted-acquisition-scrape-id";
    public const string PublishedScrapeIdFlag =
        "--interrupted-acquisition-published-scrape-id";
    public const string CurrentPublicationIdFlag =
        "--interrupted-acquisition-current-publication-id";
    public const string PreviousPublicationIdFlag =
        "--interrupted-acquisition-previous-publication-id";
    public const string WorkingPublicationIdFlag =
        "--interrupted-acquisition-working-publication-id";
    public const string WorkerInstanceIdFlag =
        "--interrupted-acquisition-worker-instance-id";
    public const string WorkerFreshnessFlag =
        "--interrupted-acquisition-worker-freshness-utc";
    public const string PhaseIdFlag =
        "--interrupted-acquisition-phase-id";
    public const string AttemptFlag =
        "--interrupted-acquisition-attempt";
    public const string AcquisitionPhaseId =
        "scrape.leaderboards";

    private static readonly HashSet<string> SwitchFlags =
        new(StringComparer.OrdinalIgnoreCase)
    {
        MaintenanceFlag,
        ExecuteFlag,
        CheckFlag,
    };

    private static readonly HashSet<string> ValueFlags =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ScrapeIdFlag,
        PublishedScrapeIdFlag,
        CurrentPublicationIdFlag,
        PreviousPublicationIdFlag,
        WorkingPublicationIdFlag,
        WorkerInstanceIdFlag,
        WorkerFreshnessFlag,
        PhaseIdFlag,
        AttemptFlag,
    };

    public static bool IsRequested(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Any(argument => argument.Equals(
            MaintenanceFlag,
            StringComparison.OrdinalIgnoreCase));
    }

    public static InterruptedAcquisitionNormalizationCommand? Parse(
        IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var relatedArgumentPresent = args.Any(argument =>
            SwitchFlags.Any(flag => MatchesFlag(argument, flag))
            || ValueFlags.Any(flag => MatchesFlag(argument, flag)));
        if (!relatedArgumentPresent)
            return null;

        EnsureOnlySupportedArguments(args);

        var maintenanceCount = Count(args, MaintenanceFlag);
        var executeCount = Count(args, ExecuteFlag);
        var checkCount = Count(args, CheckFlag);
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
        if (checkCount > 1)
        {
            throw new ArgumentException(
                $"{CheckFlag} may be specified only once.");
        }
        if (executeCount + checkCount != 1)
        {
            throw new ArgumentException(
                $"Specify exactly one of {ExecuteFlag} or {CheckFlag}.");
        }

        var scrapeId = RequirePositiveInt64(args, ScrapeIdFlag);
        var publishedScrapeId =
            RequirePositiveInt64(args, PublishedScrapeIdFlag);
        var currentPublicationId =
            RequirePositiveInt64(args, CurrentPublicationIdFlag);
        var previousPublicationId =
            RequirePositiveInt64(args, PreviousPublicationIdFlag);
        var workingPublicationId =
            RequirePositiveInt64(args, WorkingPublicationIdFlag);
        var workerInstanceId =
            RequireNonEmptyValue(args, WorkerInstanceIdFlag);
        var workerFreshnessUtc =
            RequireUtcTimestamp(args, WorkerFreshnessFlag);
        var phaseId = RequireNonEmptyValue(args, PhaseIdFlag);
        var attempt = RequirePositiveInt32(args, AttemptFlag);

        if (scrapeId == publishedScrapeId)
        {
            throw new ArgumentException(
                $"{ScrapeIdFlag} must differ from {PublishedScrapeIdFlag}.");
        }
        if (currentPublicationId == previousPublicationId
            || currentPublicationId == workingPublicationId
            || previousPublicationId == workingPublicationId)
        {
            throw new ArgumentException(
                "Expected current, previous, and working publication IDs must be distinct.");
        }
        if (!string.Equals(
                phaseId,
                AcquisitionPhaseId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{PhaseIdFlag} must be {AcquisitionPhaseId}.");
        }

        return new InterruptedAcquisitionNormalizationCommand(
            Execute: executeCount == 1,
            CheckOnly: checkCount == 1,
            ScrapeId: scrapeId,
            ExpectedPublishedScrapeId: publishedScrapeId,
            ExpectedCurrentPublicationId: currentPublicationId,
            ExpectedPreviousPublicationId: previousPublicationId,
            ExpectedWorkingPublicationId: workingPublicationId,
            ExpectedWorkerInstanceId: workerInstanceId,
            ExpectedWorkerFreshnessUtc: workerFreshnessUtc,
            ExpectedPhaseId: phaseId,
            ExpectedAttempt: attempt);
    }

    private static void EnsureOnlySupportedArguments(
        IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (SwitchFlags.Contains(argument))
                continue;

            var valueFlag = ValueFlags.FirstOrDefault(
                flag => MatchesFlag(argument, flag));
            if (valueFlag is null)
            {
                throw new ArgumentException(
                    $"Unsupported argument '{argument}' for {MaintenanceFlag}.");
            }

            if (argument.Equals(
                    valueFlag,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Count
                    || args[index + 1].StartsWith(
                        "--",
                        StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"{valueFlag} requires a value.");
                }

                index++;
            }
        }
    }

    private static bool MatchesFlag(string argument, string flag)
        => argument.Equals(
                flag,
                StringComparison.OrdinalIgnoreCase)
            || argument.StartsWith(
                flag + "=",
                StringComparison.OrdinalIgnoreCase);

    private static int Count(
        IReadOnlyList<string> args,
        string flag)
        => args.Count(argument => argument.Equals(
                flag,
                StringComparison.OrdinalIgnoreCase));

    private static string RequireSingleValue(
        IReadOnlyList<string> args,
        string flag)
    {
        var values = new List<string>();
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument.Equals(
                    flag,
                    StringComparison.OrdinalIgnoreCase))
            {
                values.Add(args[++index]);
                continue;
            }

            if (argument.StartsWith(
                    flag + "=",
                    StringComparison.OrdinalIgnoreCase))
            {
                values.Add(argument[(flag.Length + 1)..]);
            }
        }

        if (values.Count != 1)
        {
            throw new ArgumentException(
                $"{flag} must be specified exactly once.");
        }

        return values[0];
    }

    private static string RequireNonEmptyValue(
        IReadOnlyList<string> args,
        string flag)
    {
        var value = RequireSingleValue(args, flag);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                $"{flag} requires a non-empty value.");
        }

        return value.Trim();
    }

    private static long RequirePositiveInt64(
        IReadOnlyList<string> args,
        string flag)
    {
        var rawValue = RequireSingleValue(args, flag);
        if (!long.TryParse(
                rawValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value)
            || value <= 0)
        {
            throw new ArgumentException(
                $"{flag} requires a positive integer.");
        }

        return value;
    }

    private static int RequirePositiveInt32(
        IReadOnlyList<string> args,
        string flag)
    {
        var rawValue = RequireSingleValue(args, flag);
        if (!int.TryParse(
                rawValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value)
            || value <= 0)
        {
            throw new ArgumentException(
                $"{flag} requires a positive 32-bit integer.");
        }

        return value;
    }

    private static DateTime RequireUtcTimestamp(
        IReadOnlyList<string> args,
        string flag)
    {
        var rawValue = RequireSingleValue(args, flag);
        var trimmed = rawValue.Trim();
        var explicitlyUtc =
            trimmed.EndsWith(
                "Z",
                StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(
                "+00:00",
                StringComparison.Ordinal)
            || trimmed.EndsWith(
                "-00:00",
                StringComparison.Ordinal);
        if (!DateTimeOffset.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces
                | DateTimeStyles.AssumeUniversal
                | DateTimeStyles.AdjustToUniversal,
                out var value)
            || !explicitlyUtc
            || value.Offset != TimeSpan.Zero
            || value.UtcTicks % 10 != 0)
        {
            throw new ArgumentException(
                $"{flag} requires a UTC timestamp with no sub-microsecond precision.");
        }

        return value.UtcDateTime;
    }
}
