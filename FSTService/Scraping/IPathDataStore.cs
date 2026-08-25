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
