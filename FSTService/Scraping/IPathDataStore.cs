using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// Abstraction over the path data store (max scores, path generation state).
/// Implemented by PathDataStore (SQLite) and PgPathDataStore (PostgreSQL).
/// </summary>
public interface IPathDataStore
{
    Dictionary<string, (string Hash, string? LastModified)> GetPathGenerationState();
    Dictionary<string, SongMaxScores> GetAllMaxScores();
    void UpdateMaxScores(string songId, SongMaxScores scores, string datFileHash, string? songLastModified = null);
}
