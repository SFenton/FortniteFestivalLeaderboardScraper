using FSTService.Persistence;

namespace FSTService.Api;

/// <summary>
/// Gate for immediate admin path regeneration. Immediate generation promotes
/// mutable live <c>songs</c> rows outside the publication pipeline, so it must
/// never race a publication-safe staged promotion that owns a live
/// compare-and-swap at publication commit.
/// </summary>
/// <remarks>
/// Publication-bound mode is the primary rejection: while
/// <c>Scraper:UsePublicationPathArtifacts</c> is enabled, the supported ways to
/// change path state are worker scrape-pass staging, guarded max-score
/// maintenance, and the deferral rearm endpoint. The API role cannot see
/// whether a worker is staging right now, and its own
/// <c>Scraper:EnableScrapePassPathGeneration</c> value is not authoritative
/// for the worker, so the gate does not depend on either signal.
///
/// The staging-flag and working-publication rejections remain as defense in
/// depth for a misconfigured or disabled source flag.
/// </remarks>
public static class AdminPathRegenerationGate
{
    public const string PublicationBoundReason =
        "Immediate path regeneration is unavailable while publication-bound "
        + "path artifacts are the read source. Path state changes through "
        + "worker scrape-pass staging, guarded max-score maintenance, or "
        + "POST /api/admin/path-generation/rearm, never through immediate "
        + "live promotion.";

    public const string StagingEnabledReason =
        "Immediate path regeneration is unavailable while publication-safe "
        + "scrape-pass staging is enabled. Paths are staged into the working "
        + "publication and promoted at publication commit.";

    public const string WorkingPublicationReason =
        "Immediate path regeneration is unavailable while a working "
        + "publication is building. Retry after the publication commits or "
        + "fails.";

    /// <summary>
    /// Returns the conflict reason, or null when immediate regeneration is
    /// safe.
    /// </summary>
    public static string? GetConflictReason(
        ScraperOptions options,
        Func<PublicationPointerState> pointerStateProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pointerStateProvider);
        if (options.UsePublicationPathArtifacts)
            return PublicationBoundReason;

        if (options.EnableScrapePassPathGeneration)
            return StagingEnabledReason;

        return pointerStateProvider().WorkingPublicationId is null
            ? null
            : WorkingPublicationReason;
    }
}
