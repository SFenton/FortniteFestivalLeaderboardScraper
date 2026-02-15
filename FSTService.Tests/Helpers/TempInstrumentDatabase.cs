using FSTService.Persistence;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FSTService.Tests.Helpers;

/// <summary>
/// Creates an InstrumentDatabase backed by a temp-file SQLite database.
/// </summary>
public sealed class TempInstrumentDatabase : IDisposable
{
    private readonly string _dbPath;
    public InstrumentDatabase Db { get; }

    public TempInstrumentDatabase(string instrument = "Solo_Guitar")
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"fst_inst_test_{Guid.NewGuid():N}.db");
        var logger = Substitute.For<ILogger<InstrumentDatabase>>();
        Db = new InstrumentDatabase(instrument, _dbPath, logger);
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
