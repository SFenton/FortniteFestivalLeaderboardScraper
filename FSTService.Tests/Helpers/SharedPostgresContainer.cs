using Npgsql;
using Testcontainers.PostgreSql;

namespace FSTService.Tests.Helpers;

/// <summary>
/// Shared PostgreSQL Testcontainer — one container per test run.
/// Lazily started on first use, shared across all test classes.
/// </summary>
public static class SharedPostgresContainer
{
    private static readonly Lazy<string> _isolatedConnection = new(ValidateIsolatedConnection);
    private static readonly Lazy<PostgreSqlContainer> _container = new(() =>
    {
        var container = ControlledPostgresTestFixture.CreateBuilder("primary")
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
        ControlledPostgresTestFixture.RecordStartedAsync(container).GetAwaiter().GetResult();
        return container;
    });

    public static string ConnectionString =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
            "FST_TEST_POSTGRES_CONNECTION_STRING"))
            ? _container.Value.GetConnectionString()
            : _isolatedConnection.Value;

    private static string ValidateIsolatedConnection()
    {
        var scope = Environment.GetEnvironmentVariable("FST_TEST_POSTGRES_SCOPE");
        if (!Guid.TryParseExact(scope, "D", out _))
            throw new InvalidOperationException("An exact disposable PostgreSQL test scope is required.");
        var builder = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("FST_TEST_POSTGRES_CONNECTION_STRING"))
        {
            Options = "",
            ApplicationName = "fst-isolated-tests",
            Timeout = 5,
            CommandTimeout = 30,
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = 10,
        };
        const string socketRoot =
            "/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/.s/";
        if (string.IsNullOrWhiteSpace(builder.Host))
            throw new InvalidOperationException("The disposable PostgreSQL socket is required.");
        var host = Path.GetFullPath(builder.Host);
        if (!host.StartsWith(socketRoot, StringComparison.Ordinal)
            || host[socketRoot.Length..].Length != 12
            || host[socketRoot.Length..].Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            || builder.Database != "fst_offline_report_tests"
            || builder.Username != "fst_test")
        {
            throw new InvalidOperationException("The test override must identify an owned FST-drive Unix socket.");
        }
        using var connection = new NpgsqlConnection(builder.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = """
            SELECT pg_catalog.current_setting('fst.offline_report_test_scope', true),
                current_database(), current_user,
                pg_catalog.current_setting('server_version_num')::INTEGER,
                pg_catalog.inet_server_addr() IS NULL
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0) || reader.GetString(0) != scope
            || reader.GetString(1) != "fst_offline_report_tests"
            || reader.GetString(2) != "fst_test"
            || reader.GetInt32(3) is < 170000 or >= 180000 || !reader.GetBoolean(4))
        {
            throw new InvalidOperationException("The disposable PostgreSQL test identity did not match.");
        }
        return builder.ConnectionString;
    }

    /// <summary>
    /// Creates a fresh database with a unique name and returns a data source for it.
    /// Each data source uses a minimal connection pool to avoid exhausting the container.
    /// Safe to call from synchronous test constructors.
    /// </summary>
    public static NpgsqlDataSource CreateDatabase(
        int maxPoolSize = 10)
    {
        var connectionString =
            CreateEmptyDatabaseConnectionString(
                "fst_",
                maxPoolSize);
        var ds = NpgsqlDataSource.Create(connectionString);

        // Initialize schema after the freshly-created database accepts connections.
        try
        {
            ExecuteWithPostgresReadyRetry(() =>
                FSTService.Persistence.DatabaseInitializer.EnsureSchemaAsync(ds)
                    .GetAwaiter().GetResult());
            ExecuteWithPostgresReadyRetry(() =>
                new FSTService.Persistence.FestivalPersistence(ds)
                    .SaveSongsVersionedAsync([])
                    .GetAwaiter().GetResult());
        }
        catch
        {
            ds.Dispose();
            throw;
        }

        return ds;
    }

    public static string CreateEmptyDatabaseConnectionString(
        string prefix = "fst_replay_",
        int maxPoolSize = 10)
    {
        var connStr = ConnectionString;
        var dbName = $"{prefix}{Guid.NewGuid():N}";
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
            MaxPoolSize = maxPoolSize,
            ConnectionIdleLifetime = 10,
            PersistSecurityInfo = true,
        };
        return builder.ConnectionString;
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
