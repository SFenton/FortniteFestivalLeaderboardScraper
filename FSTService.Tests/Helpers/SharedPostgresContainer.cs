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
            // Raise max_connections for parallel test classes, and keep test
            // queries off Docker's small default /dev/shm allocation.
            .WithCommand(
                "-c", "max_connections=500",
                "-c", "max_parallel_workers=0",
                "-c", "max_parallel_workers_per_gather=0",
                "-c", "dynamic_shared_memory_type=mmap")
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
        ExecuteWithPostgresReadyRetry(() =>
        {
            using var conn = new NpgsqlConnection(connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{dbName}\";";
            cmd.ExecuteNonQuery();
        });

        var builder = new NpgsqlConnectionStringBuilder(connStr)
        {
            Database = dbName,
            MinPoolSize = 0,
            MaxPoolSize = 10,
            ConnectionIdleLifetime = 10,
        };
        var ds = NpgsqlDataSource.Create(builder.ConnectionString);

        // Initialize schema after the freshly-created database accepts connections.
        try
        {
            ExecuteWithPostgresReadyRetry(() =>
                FSTService.Persistence.DatabaseInitializer.EnsureSchemaAsync(ds)
                    .GetAwaiter().GetResult());
        }
        catch
        {
            ds.Dispose();
            throw;
        }

        return ds;
    }

    private static void ExecuteWithPostgresReadyRetry(Action action)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        var delay = TimeSpan.FromMilliseconds(100);
        Exception? lastException = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (IsPostgresStarting(ex))
            {
                lastException = ex;
                Thread.Sleep(delay);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, 1000));
            }
        }

        throw new TimeoutException("PostgreSQL test container did not become ready in time.", lastException);
    }

    private static bool IsPostgresStarting(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgresException)
                return postgresException.SqlState == "57P03";

            if (current is NpgsqlException)
                return true;
        }

        return false;
    }
}
