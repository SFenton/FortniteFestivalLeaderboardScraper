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
    Task<PathGenerationPromotionOutcome> TryPromoteGenerationAsync(
        PathGenerationPromotion promotion,
        CancellationToken ct);
    Task AppendPathGenerationErrorAsync(
        PathGenerationError error,
        CancellationToken ct);
}
