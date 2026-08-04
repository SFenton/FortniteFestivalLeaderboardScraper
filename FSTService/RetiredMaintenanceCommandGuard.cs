namespace FSTService;

internal static class RetiredMaintenanceCommandGuard
{
    private static readonly HashSet<string> RetiredOptions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "path-repair-stage-exact-four",
            "path-repair-align-rankings",
            "path-repair-promote-exact-four",
            "path-repair-rebuild-rankings",
            "path-repair-manifest",
            "path-repair-manifest-output",
            "path-repair-rollback-output",
            "notification-maintenance-pro-lead-max-score-repair",
            "notification-maintenance-execute",
            "notification-maintenance-manifest",
            "expected-notification-dry-run-digest",
            "notification-reopen-completed",
        };

    private static readonly HashSet<string> ManualStandaloneOptions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "recover-improvement-notifications",
            "initialize-schema-only",
            "api-only",
            "no-scraper-worker",
            "registration-sync-worker",
            "once",
            "backfill-only",
            "setup",
            "resolve-only",
            "precompute",
            "solo-scrape",
            "solo-enrichment",
            "solo-refresh-users",
            "solo-leaderboards",
            "solo-rivals",
            "solo-player-stats",
            "solo-precompute",
            "solo-finalize",
            "band-scrape",
            "band-post-scrape",
            "band-extraction",
            "notification-dry-run",
            "notification-baseline-only",
            "notification-skip-projection-refresh",
            "notification-force",
            "score-history-dedup-maintenance",
            "score-history-dedup-execute",
            "solo-family-ranking-backfill",
            "solo-family-ranking-backfill-execute",
        };

    public static void ThrowIfPresent(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (string.IsNullOrWhiteSpace(argument))
                continue;

            if (!TryParseOption(
                    argument,
                    out var optionToken,
                    out var canonicalOption,
                    out var consumesSeparateValue))
            {
                continue;
            }

            if (!RetiredOptions.Contains(canonicalOption))
            {
                // The Microsoft command-line provider consumes the next token
                // for non-inline "--" and "/" keys. Program's explicit
                // standalone flags are the only double-dash exceptions.
                if (consumesSeparateValue
                    && !IsManualStandaloneOption(
                        optionToken,
                        canonicalOption)
                    && index + 1 < args.Count)
                {
                    index++;
                }
                continue;
            }

            throw new ArgumentException(
                $"Retired maintenance option '{optionToken}' is no longer " +
                "executable. Refusing startup before hosted scraper mode " +
                "selection.");
        }
    }

    private static bool TryParseOption(
        string argument,
        out string optionToken,
        out string canonicalOption,
        out bool consumesSeparateValue)
    {
        var equalsIndex = argument.IndexOf('=');
        optionToken = equalsIndex >= 0
            ? argument[..equalsIndex]
            : argument;

        var prefixLength = 0;
        if (optionToken.StartsWith("--", StringComparison.Ordinal))
            prefixLength = 2;
        else if (optionToken.StartsWith("-", StringComparison.Ordinal)
                 || optionToken.StartsWith("/", StringComparison.Ordinal))
            prefixLength = 1;
        else if (equalsIndex < 0)
        {
            canonicalOption = "";
            consumesSeparateValue = false;
            return false;
        }

        canonicalOption = optionToken[prefixLength..];
        consumesSeparateValue =
            equalsIndex < 0
            && (prefixLength == 2
                || optionToken.StartsWith("/", StringComparison.Ordinal));
        return canonicalOption.Length > 0;
    }

    private static bool IsManualStandaloneOption(
        string optionToken,
        string canonicalOption)
        => optionToken.StartsWith("--", StringComparison.Ordinal)
           && ManualStandaloneOptions.Contains(canonicalOption);
}
