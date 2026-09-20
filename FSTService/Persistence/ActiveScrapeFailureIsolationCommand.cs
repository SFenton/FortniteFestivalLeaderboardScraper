using System.Globalization;

namespace FSTService.Persistence;

public sealed record ActiveScrapeFailureIsolationCommand(
    bool Execute,
    bool CheckOnly,
    long ScrapeId,
    long ExpectedPublishedScrapeId,
    string? FailurePhase,
    string? FailureMessage)
{
    public const string MaintenanceFlag =
        "--active-scrape-failure-isolation";
    public const string ExecuteFlag =
        "--active-scrape-failure-isolation-execute";
    public const string CheckFlag =
        "--active-scrape-failure-isolation-check";
    public const string ScrapeIdFlag =
        "--active-scrape-id";
    public const string FailurePhaseFlag =
        "--active-scrape-failure-phase";
    public const string FailureMessageFlag =
        "--active-scrape-failure-message";

    private static readonly HashSet<string> SupportedFailurePhases =
    [
        MetaDatabase.NoProgressReadIsolationFailurePhase,
        MetaDatabase.FailedCandidateReadIsolationFailurePhase,
        MetaDatabase.AcquisitionFailureIsolationFailurePhase,
        MetaDatabase.PostProcessReadIsolationFailurePhase,
        MetaDatabase.PublicationReadIsolationFailurePhase,
        MetaDatabase.StalePublicationCommitIntentFailurePhase,
    ];

    public static ActiveScrapeFailureIsolationCommand? Parse(
        IReadOnlyList<string> args,
        PublishedScrapeIdArgument publishedScrapeId)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(publishedScrapeId);

        var maintenanceCount = Count(args, MaintenanceFlag);
        var executeCount = Count(args, ExecuteFlag);
        var checkCount = Count(args, CheckFlag);
        var scrapeIdValues = GetValues(args, ScrapeIdFlag);
        var failurePhaseValues = GetValues(args, FailurePhaseFlag);
        var failureMessageValues = GetValues(args, FailureMessageFlag);

        if (maintenanceCount == 0
            && executeCount == 0
            && checkCount == 0
            && scrapeIdValues.Count == 0
            && failurePhaseValues.Count == 0
            && failureMessageValues.Count == 0)
        {
            return null;
        }

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

        var scrapeId = RequirePositiveInteger(
            scrapeIdValues,
            ScrapeIdFlag);
        var expectedPublishedScrapeId =
            publishedScrapeId.RequireValue(MaintenanceFlag);
        if (checkCount == 1)
        {
            if (failurePhaseValues.Count != 0
                || failureMessageValues.Count != 0)
            {
                throw new ArgumentException(
                    $"{CheckFlag} cannot be combined with {FailurePhaseFlag} or {FailureMessageFlag}.");
            }

            return new ActiveScrapeFailureIsolationCommand(
                Execute: false,
                CheckOnly: true,
                ScrapeId: scrapeId,
                ExpectedPublishedScrapeId: expectedPublishedScrapeId,
                FailurePhase: null,
                FailureMessage: null);
        }

        var failurePhase = RequireSingleValue(
            failurePhaseValues,
            FailurePhaseFlag);
        if (!IsSupportedFailurePhase(failurePhase))
        {
            throw new ArgumentException(
                $"{FailurePhaseFlag} must be one of {string.Join(", ", SupportedFailurePhases.Order())}.");
        }
        var failureMessage = RequireSingleValue(
            failureMessageValues,
            FailureMessageFlag);
        if (string.IsNullOrWhiteSpace(failureMessage))
        {
            throw new ArgumentException(
                $"{FailureMessageFlag} requires a non-empty value.");
        }

        return new ActiveScrapeFailureIsolationCommand(
            Execute: executeCount == 1,
            CheckOnly: false,
            ScrapeId: scrapeId,
            ExpectedPublishedScrapeId: expectedPublishedScrapeId,
            FailurePhase: failurePhase,
            FailureMessage: failureMessage.Trim());
    }

    internal static bool IsSupportedFailurePhase(string failurePhase)
        => SupportedFailurePhases.Contains(failurePhase);

    private static int Count(
        IReadOnlyList<string> args,
        string flag)
        => args.Count(argument => argument.Equals(
            flag,
            StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> GetValues(
        IReadOnlyList<string> args,
        string flag)
    {
        var values = new List<string>();
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument.Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Count
                    || args[index + 1].StartsWith(
                        "--",
                        StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"{flag} requires a value.");
                }

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

        return values;
    }

    private static string RequireSingleValue(
        IReadOnlyList<string> values,
        string flag)
    {
        if (values.Count != 1)
        {
            throw new ArgumentException(
                $"{flag} must be specified exactly once.");
        }

        return values[0];
    }

    private static long RequirePositiveInteger(
        IReadOnlyList<string> values,
        string flag)
    {
        var rawValue = RequireSingleValue(values, flag);
        if (string.IsNullOrWhiteSpace(rawValue)
            || !long.TryParse(
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
}
