using System.Globalization;

namespace FSTService.Persistence;

public sealed record PublishedScrapeIdArgument(
    bool IsPresent,
    long? Value)
{
    public const string Flag = "--published-scrape-id";

    public static PublishedScrapeIdArgument Parse(
        IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? rawValue = null;
        var occurrences = 0;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument.Equals(
                    Flag,
                    StringComparison.OrdinalIgnoreCase))
            {
                occurrences++;
                if (index + 1 >= args.Count
                    || args[index + 1].StartsWith(
                        "--",
                        StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"{Flag} requires a value.");
                }
                rawValue = args[++index];
                continue;
            }

            if (argument.StartsWith(
                    Flag + "=",
                    StringComparison.OrdinalIgnoreCase))
            {
                occurrences++;
                rawValue = argument[(Flag.Length + 1)..];
            }
        }

        if (occurrences == 0)
            return new PublishedScrapeIdArgument(false, null);
        if (occurrences != 1)
        {
            throw new ArgumentException(
                $"{Flag} must be specified exactly once.");
        }
        if (string.IsNullOrWhiteSpace(rawValue)
            || !long.TryParse(
                rawValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value)
            || value <= 0)
        {
            throw new ArgumentException(
                $"{Flag} requires a positive integer.");
        }

        return new PublishedScrapeIdArgument(true, value);
    }

    public long RequireValue(string command)
        => IsPresent && Value.HasValue
            ? Value.Value
            : throw new ArgumentException(
                $"{command} requires exactly one {Flag} value.");

    public void RejectIfOrphaned(bool hasOwningCommand)
    {
        if (IsPresent && !hasOwningCommand)
        {
            throw new ArgumentException(
                $"{Flag} is only valid with notification recovery or max-score maintenance.");
        }
    }
}
