using Npgsql;
using Testcontainers.PostgreSql;

namespace FSTService.Tests.Helpers;

/// <summary>
/// Shared PostgreSQL Testcontainer — one container per test run.
/// Lazily started on first use, shared across all test classes.
/// </summary>
public static class SharedPostgresContainer
{
    private static readonly Lazy<PostgreSqlContainer> _container = new(() =>
    {
        var container = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("fst_tests")
            .WithUsername("test")
            .WithPassword("test")
            // Raise max_connections to handle parallel test classes
            .WithCommand("-c", "max_connections=500")
            .Build();
        container.StartAsync().GetAwaiter().GetResult();
        return container;
    });

    public static string ConnectionString => _container.Value.GetConnectionString();

    /// <summary>
    /// Creates a fresh database with a unique name and returns a data source for it.
    /// Each data source uses a minimal connection pool to avoid exhausting the container.
    /// Safe to call from synchronous test constructors.
    /// </summary>
    public static NpgsqlDataSource CreateDatabase()
    {
        var connStr = ConnectionString;
        var dbName = $"fst_{Guid.NewGuid():N}";
        using var conn = new NpgsqlConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{dbName}\";";
        cmd.ExecuteNonQuery();

        var builder = new NpgsqlConnectionStringBuilder(connStr)
        {
            Database = dbName,
            MinPoolSize = 0,
            MaxPoolSize = 10,
            ConnectionIdleLifetime = 10,
        };
        var ds = NpgsqlDataSource.Create(builder.ConnectionString);

        // Initialize schema
        FSTService.Persistence.DatabaseInitializer.EnsureSchemaAsync(ds)
            .GetAwaiter().GetResult();

        return ds;
    }
}
