namespace FSTService;

/// <summary>
/// Individual phases of the scrape pipeline, used to selectively enable/disable
/// phases via CLI flags (<c>--solo-scrape</c>, <c>--band-scrape</c>, <c>--solo-leaderboards</c>).
/// Phases prefixed <c>Solo</c> are on the solo instrument chain; phases prefixed
/// <c>Band</c> are on the band chain. When no flags are specified, all phases run.
/// </summary>
[Flags]
public enum ScrapePhase
{
    None             = 0,

    // ── Solo chain (ordered 1–8) ──
    SoloScrape       = 1 << 0,
    SoloEnrichment   = 1 << 1,
    SoloRefreshUsers = 1 << 2,
    SoloRankings     = 1 << 3,
    SoloRivals       = 1 << 4,
    SoloPlayerStats  = 1 << 5,
    SoloPrecompute   = 1 << 6,
    SoloFinalize     = 1 << 7,

    // ── Band chain (ordered 1–3) ──
    BandScrape       = 1 << 8,
    BandScrapePhase  = 1 << 9,
    BandExtraction   = 1 << 10,

    // ── Convenience groups ──
    SoloAll = SoloScrape | SoloEnrichment | SoloRefreshUsers | SoloRankings
            | SoloRivals | SoloPlayerStats | SoloPrecompute | SoloFinalize,

    BandAll = BandScrape | BandScrapePhase | BandExtraction,

    All = SoloAll | BandAll,
}

/// <summary>
/// Resolves CLI-specified <see cref="ScrapePhase"/> flags into the full set of
/// phases that should execute, applying group expansion and intermediary filling.
/// </summary>
public static class ScrapePhaseResolver
{
    /// <summary>CLI flag <c>--solo-scrape</c> enables these phases.</summary>
    public const ScrapePhase SoloScrapeGroup =
        ScrapePhase.SoloScrape | ScrapePhase.SoloEnrichment | ScrapePhase.SoloRefreshUsers;

    /// <summary>CLI flag <c>--band-scrape</c> enables these phases.</summary>
    public const ScrapePhase BandScrapeGroup =
        ScrapePhase.BandScrape | ScrapePhase.BandScrapePhase | ScrapePhase.BandExtraction;

    /// <summary>CLI flag <c>--solo-leaderboards</c> enables these phases.</summary>
    public const ScrapePhase SoloLeaderboardsGroup =
        ScrapePhase.SoloRankings | ScrapePhase.SoloRivals | ScrapePhase.SoloPlayerStats
        | ScrapePhase.SoloPrecompute | ScrapePhase.SoloFinalize;

    /// <summary>Solo chain phases in pipeline order (position 1–8).</summary>
    private static readonly ScrapePhase[] SoloChain =
    [
        ScrapePhase.SoloScrape,
        ScrapePhase.SoloEnrichment,
        ScrapePhase.SoloRefreshUsers,
        ScrapePhase.SoloRankings,
        ScrapePhase.SoloRivals,
        ScrapePhase.SoloPlayerStats,
        ScrapePhase.SoloPrecompute,
        ScrapePhase.SoloFinalize,
    ];

    /// <summary>
    /// Expand raw CLI flags into the full resolved phase set.
    /// <list type="bullet">
    ///   <item><see cref="ScrapePhase.None"/> (no flags) → <see cref="ScrapePhase.All"/></item>
    ///   <item>Each primary flag expands to its group (e.g. <c>SoloScrape</c> → <c>SoloScrapeGroup</c>)</item>
    ///   <item>Intermediary filling: on the solo chain, enable all phases between the min and max enabled positions</item>
    /// </list>
    /// </summary>
    public static ScrapePhase Resolve(ScrapePhase requested)
    {
        if (requested == ScrapePhase.None)
            return ScrapePhase.All;

        var result = requested;

        // Expand primary flags to their groups
        if (result.HasFlag(ScrapePhase.SoloScrape))
            result |= SoloScrapeGroup;
        if (result.HasFlag(ScrapePhase.SoloRankings))
            result |= SoloLeaderboardsGroup;
        if (result.HasFlag(ScrapePhase.BandScrape))
            result |= BandScrapeGroup;

        // Intermediary filling on the solo chain: find min/max enabled
        // positions and enable everything between them.
        int minPos = -1, maxPos = -1;
        for (int i = 0; i < SoloChain.Length; i++)
        {
            if (result.HasFlag(SoloChain[i]))
            {
                if (minPos < 0) minPos = i;
                maxPos = i;
            }
        }

        if (minPos >= 0)
        {
            for (int i = minPos; i <= maxPos; i++)
                result |= SoloChain[i];
        }

        return result;
    }

    /// <summary>
    /// Format resolved phases as a human-readable pipe-delimited string for logging.
    /// </summary>
    public static string Format(ScrapePhase phases)
    {
        if (phases == ScrapePhase.All || phases == (ScrapePhase.All | ScrapePhase.None))
            return "All (full pipeline)";

        var names = new List<string>();
        foreach (ScrapePhase value in Enum.GetValues<ScrapePhase>())
        {
            // Skip None, composite groups
            if (value == ScrapePhase.None || value == ScrapePhase.SoloAll
                || value == ScrapePhase.BandAll || value == ScrapePhase.All)
                continue;

            if (phases.HasFlag(value))
                names.Add(value.ToString());
        }

        return string.Join(" | ", names);
    }
}
