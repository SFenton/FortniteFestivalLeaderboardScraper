using FSTService.Persistence;
using Microsoft.Extensions.Logging;
using Npgsql;
using NSubstitute;

namespace FSTService.Tests.Helpers;

/// <summary>
/// Creates a MetaDatabase backed by a fresh PostgreSQL database.
/// Drop-in replacement for the old SQLite-backed fixture.
/// </summary>
public sealed class InMemoryMetaDatabase : IDisposable
{
    private readonly NpgsqlDataSource _ds;
    public MetaDatabase Db { get; }

    public InMemoryMetaDatabase()
    {
        _ds = SharedPostgresContainer.CreateDatabase();
        var logger = Substitute.For<ILogger<MetaDatabase>>();
        Db = new MetaDatabase(_ds, logger);
    }

    /// <summary>The underlying NpgsqlDataSource (for tests that need direct PG access).</summary>
    public NpgsqlDataSource DataSource => _ds;

    public void Dispose()
    {
        Db.Dispose();
        _ds.Dispose();
    }
}
