using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// Abstraction over the path data store (max scores, path generation state).
/// </summary>
public interface IPathDataStore
{
    Dictionary<string, PathGenerationState> GetPathGenerationStates();
    PathGenerationState? GetPathGenerationState(string songId);
    HashSet<string> GetPendingPathGenerationSongIds();
    Dictionary<string, SongMaxScores> GetAllMaxScores();
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
