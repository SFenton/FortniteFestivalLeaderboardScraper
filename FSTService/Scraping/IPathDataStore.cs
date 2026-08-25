using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// Abstraction over the path data store (max scores, path generation state).
/// </summary>
/// <remarks>
/// The unqualified read members return <em>effective</em> state: the
/// publication-bound snapshot when
/// <c>Scraper:UsePublicationPathArtifacts</c> is enabled, and live
/// <c>songs</c> rows otherwise. Mutation, generation, and maintenance code
/// paths must use the explicit <c>*Live*</c> members so they always observe
/// live rows.
/// </remarks>
public interface IPathDataStore
{
    Dictionary<string, PathGenerationState> GetPathGenerationStates();
    PathGenerationState? GetPathGenerationState(string songId);
    HashSet<string> GetPendingPathGenerationSongIds();
    Dictionary<string, SongMaxScores> GetAllMaxScores();

    /// <summary>Live <c>songs</c> path generation state for all songs.</summary>
    Dictionary<string, PathGenerationState> GetLivePathGenerationStates()
        => GetPathGenerationStates();

    /// <summary>Live <c>songs</c> path generation state for one song.</summary>
    PathGenerationState? GetLivePathGenerationState(string songId)
        => GetPathGenerationState(songId);

    /// <summary>Live <c>songs</c> max scores for all songs.</summary>
    Dictionary<string, SongMaxScores> GetLiveAllMaxScores()
        => GetAllMaxScores();

    /// <summary>
    /// Scopes effective reads on the current asynchronous flow to a specific
    /// publication snapshot. Disposing restores the previous scope.
    /// </summary>
    IDisposable BeginPublicationRead(long publicationId)
        => PathDataStorePublicationScope.NoOp;

    /// <summary>
    /// Pending songs that automatic scrape-pass staging may attempt now.
    /// Excludes songs deferred for review or backoff whose deferral is still
    /// bound to the current provider catalog identity.
    /// </summary>
    IReadOnlyList<PathGenerationCandidate>
        GetAutomaticPathGenerationCandidates(DateTime nowUtc)
        => GetPendingPathGenerationSongIds()
            .OrderBy(static songId => songId, StringComparer.Ordinal)
            .Select(static songId => new PathGenerationCandidate(songId, 0))
            .ToArray();

    /// <summary>
    /// Durably defers a song for explicit review without clearing
    /// <c>path_generation_pending</c>. The deferral is bound to the provider
    /// catalog identity it was recorded against, so a later catalog change
    /// re-arms the song automatically.
    /// </summary>
    Task MarkPathGenerationReviewRequiredAsync(
        string songId,
        string reason,
        string? catalogIdentity,
        CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Durably schedules the next automatic attempt for a song after a
    /// deterministic generation failure, using bounded exponential backoff.
    /// A stale review-required deferral is replaced by this ordinary retry:
    /// the song was attempted again, so the recorded reason and the gate both
    /// become the retry outcome instead of the obsolete review decision.
    /// </summary>
    Task SchedulePathGenerationRetryAsync(
        string songId,
        string reason,
        string? catalogIdentity,
        CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Operator reset: clears review-required and backoff state so automatic
    /// staging may attempt the song again. Returns false when no song matched.
    /// </summary>
    bool RearmPathGeneration(string songId) => false;

    /// <summary>Current deferral state, for operator inspection.</summary>
    PathGenerationDeferralState? GetPathGenerationDeferralState(string songId)
        => null;

    void InvalidateCachedState()
    {
    }
    Task<PathGenerationPromotionOutcome> TryPromoteGenerationAsync(
        PathGenerationPromotion promotion,
        CancellationToken ct);
    Task<PathGenerationBatchPromotionResult>
        TryPromoteGenerationsAtomicallyAsync(
            IReadOnlyList<PathGenerationPromotion> promotions,
            CancellationToken ct)
        => throw new NotSupportedException(
            "Atomic path generation promotion is not supported by this store.");
    Task<PathGenerationBatchPromotionResult>
        TryPromoteGenerationsAtomicallyAsync(
            IReadOnlyList<PathGenerationPromotion> promotions,
            PathGenerationBatchPromotionGate gate,
            IMaxScoreMaintenanceLease maintenanceLease,
            CancellationToken ct)
        => throw new NotSupportedException(
            "Fenced atomic path generation promotion is not supported by this store.");
    Task AppendPathGenerationErrorAsync(
        PathGenerationError error,
        CancellationToken ct);
}

/// <summary>
/// A pending song that automatic staging may attempt, with the number of
/// consecutive deferred attempts already recorded against it.
/// </summary>
public sealed record PathGenerationCandidate(
    string SongId,
    int AttemptCount);

/// <summary>
/// Durable automatic-staging deferral state for one song. <c>Pending</c> is
/// never cleared by a deferral: it stays true so the work remains auditable.
/// </summary>
public sealed record PathGenerationDeferralState(
    string SongId,
    bool Pending,
    bool ReviewRequired,
    string? ReviewReason,
    DateTime? ReviewAtUtc,
    DateTime? NextAttemptAtUtc,
    int AttemptCount,
    string? DeferralIdentity);

/// <summary>
/// Thrown when an explicitly scoped publication read has no bound path
/// artifact snapshot. Publication reads fail closed instead of silently
/// falling back to another publication or to mutable live rows.
/// </summary>
public sealed class PublicationPathArtifactsUnavailableException
    : InvalidOperationException
{
    public PublicationPathArtifactsUnavailableException(long publicationId)
        : base(
            $"Publication {publicationId} has no bound path artifact snapshot.")
    {
        PublicationId = publicationId;
    }

    public long PublicationId { get; }
}
