using FSTService.Persistence;
using Microsoft.Extensions.Logging;
using Npgsql;
using NSubstitute;

namespace FSTService.Tests.Helpers;

/// <summary>
/// Creates an InstrumentDatabase backed by a fresh PostgreSQL database.
/// Drop-in replacement for the old SQLite-backed fixture.
/// </summary>
public sealed class TempInstrumentDatabase : IDisposable
{
    private readonly NpgsqlDataSource _ds;
    public InstrumentDatabase Db { get; }

    public TempInstrumentDatabase(string instrument = "Solo_Guitar")
    {
        _ds = SharedPostgresContainer.CreateDatabase();
        var logger = Substitute.For<ILogger<InstrumentDatabase>>();
        Db = new InstrumentDatabase(instrument, _ds, logger);
    }

    /// <summary>The underlying NpgsqlDataSource (for tests that need direct PG access).</summary>
    public NpgsqlDataSource DataSource => _ds;

    public void Dispose()
    {
        Db.Dispose();
        _ds.Dispose();
    }
}
