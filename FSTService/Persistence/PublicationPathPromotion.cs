using FSTService.Scraping;

namespace FSTService.Persistence;

/// <summary>
/// Result of applying one validated staged generation to a working
/// publication path artifact snapshot.
/// </summary>
public enum PublicationPathPromotionOutcome
{
    /// <summary>The candidate snapshot row was replaced and rebound.</summary>
    Applied,

    /// <summary>
    /// The snapshot row no longer matches the state the generation was staged
    /// against, or already carries a staged promotion.
    /// </summary>
    Conflict,

    /// <summary>The publication snapshot has no row for the song.</summary>
    SongMissing,

    /// <summary>
    /// The publication is no longer the building working publication of the
    /// requested scrape.
    /// </summary>
    PublicationNotStaging,
}

/// <summary>
/// One validated staged generation plus the exact candidate state it was
/// staged against. Every field participates in the snapshot compare-and-swap.
/// </summary>
public sealed record PublicationPathPromotionRequest(
    long PublicationId,
    long ScrapeId,
    string SongId,
    long ExpectedRevision,
    string? ExpectedGenerationId,
    string? ExpectedCatalogLastModified,
    PathGenerationPromotion Promotion);

/// <summary>
/// Durable staged-promotion metadata read back from
/// <c>publication_path_artifacts</c>.
/// </summary>
public sealed record PublicationPathPromotionRow(
    string SongId,
    long CandidateRevision,
    string? CandidateGenerationId,
    long? ExpectedLiveRevision,
    string? ExpectedLiveGenerationId,
    string? PromotionAttemptId,
    string? PromotionSource,
    string? CatalogLastModified);

/// <summary>
/// Thrown when the live compare-and-swap for staged path promotions does not
/// affect exactly the expected number of rows during a publication commit.
/// This is never retried and never converted to a busy/deferred outcome: the
/// commit transaction rolls back and the candidate is failed and isolated.
/// </summary>
public sealed class PublicationPathPromotionConflictException
    : InvalidOperationException
{
    public PublicationPathPromotionConflictException(
        long publicationId,
        int expectedCount,
        int promotedCount)
        : base(
            $"Publication {publicationId} staged path promotion updated "
            + $"{promotedCount} live song row(s); {expectedCount} were "
            + "required. The live path generation identity changed after "
            + "staging.")
    {
        PublicationId = publicationId;
        ExpectedCount = expectedCount;
        PromotedCount = promotedCount;
    }

    public long PublicationId { get; }
    public int ExpectedCount { get; }
    public int PromotedCount { get; }
}
