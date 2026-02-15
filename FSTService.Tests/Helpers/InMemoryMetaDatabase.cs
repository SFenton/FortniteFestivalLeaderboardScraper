using FSTService.Persistence;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FSTService.Tests.Helpers;

/// <summary>
/// Creates a MetaDatabase backed by a temp-file SQLite database.
/// Automatically cleaned up on dispose.
/// </summary>
public sealed class InMemoryMetaDatabase : IDisposable
{
    private readonly string _dbPath;
    public MetaDatabase Db { get; }

    public InMemoryMetaDatabase()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"fst_test_{Guid.NewGuid():N}.db");
        var logger = Substitute.For<ILogger<MetaDatabase>>();
        Db = new MetaDatabase(_dbPath, logger);
        Db.EnsureSchema();
    }

    public void Dispose()
    {
        Db.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }
}
