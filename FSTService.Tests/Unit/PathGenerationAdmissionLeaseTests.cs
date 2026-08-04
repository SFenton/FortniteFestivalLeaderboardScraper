using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FSTService.Tests.Unit;

public sealed class PathGenerationAdmissionLeaseTests
{
    [Fact]
    public void AdvisoryLockKeyIsDistinctFromPublicationLocks()
    {
        Assert.True(
            PathGenerationAdmissionLock.AdvisoryLockKey
            < PublicationGenerationSchema.AdvisoryLockKey);
        Assert.True(
            PathGenerationAdmissionLock.AdvisoryLockKey
            < PublicationGenerationSchema.CacheBuildAdvisoryLockBase);
    }

    [Fact]
    public async Task PostgreSqlLeaseSerializesIndependentCallers()
    {
        using var database = new InMemoryMetaDatabase();
        var firstProvider = CreateProvider(database);
        var secondProvider = CreateProvider(database);
        var firstLease = await firstProvider.AcquireAsync(
            CancellationToken.None);

        try
        {
            var secondAcquire = secondProvider.AcquireAsync(
                CancellationToken.None);
            var completed = await Task.WhenAny(
                secondAcquire,
                Task.Delay(TimeSpan.FromMilliseconds(250)));

            Assert.NotSame(secondAcquire, completed);

            await firstLease.DisposeAsync();
            firstLease = NoopAsyncDisposable.Instance;
            await using var secondLease = await secondAcquire.WaitAsync(
                TimeSpan.FromSeconds(5));
        }
        finally
        {
            await firstLease.DisposeAsync();
        }
    }

    [Fact]
    public async Task PostgreSqlLeaseWaitIsCancellableAndReleasesConnection()
    {
        using var database = new InMemoryMetaDatabase();
        var firstProvider = CreateProvider(database);
        var secondProvider = CreateProvider(database);
        var firstLease = await firstProvider.AcquireAsync(
            CancellationToken.None);

        try
        {
            using var cancellation = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => secondProvider.AcquireAsync(cancellation.Token));

            await firstLease.DisposeAsync();
            firstLease = NoopAsyncDisposable.Instance;
            await using var recoveredLease =
                await secondProvider
                    .AcquireAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await firstLease.DisposeAsync();
        }
    }

    [Fact]
    public async Task SameProviderAdmissionDoesNotConsumeTenConnectionPool()
    {
        using var database = new InMemoryMetaDatabase();
        var provider = CreateProvider(database);
        var holder = await provider.AcquireAsync(CancellationToken.None);
        using var waiterCancellation = new CancellationTokenSource();
        var waiters = Enumerable
            .Range(0, 12)
            .Select(_ => provider.AcquireAsync(waiterCancellation.Token))
            .ToArray();
        var probes = new List<NpgsqlConnection>();

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            using var probeTimeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(5));
            for (var index = 0; index < 10; index++)
            {
                var connection = await database.DataSource
                    .OpenConnectionAsync(probeTimeout.Token);
                probes.Add(connection);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1";
                Assert.Equal(
                    1,
                    Convert.ToInt32(
                        await command.ExecuteScalarAsync(
                            probeTimeout.Token)));
            }

            waiterCancellation.Cancel();
            foreach (var waiter in waiters)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => waiter);
            }

            foreach (var connection in probes)
                await connection.DisposeAsync();
            probes.Clear();

            await holder.DisposeAsync();
            holder = NoopAsyncDisposable.Instance;
            await using var recoveredLease =
                await provider
                    .AcquireAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            waiterCancellation.Cancel();
            foreach (var connection in probes)
                await connection.DisposeAsync();
            await holder.DisposeAsync();
            await ObserveWaitersAsync(waiters);
        }
    }

    [Fact]
    public async Task BrokenDedicatedLeaseSessionCannotStrandAdmission()
    {
        using var database = new InMemoryMetaDatabase();
        var provider = CreateProvider(database);
        var lease = await provider.AcquireAsync(CancellationToken.None);

        try
        {
            await using (var control = await database.DataSource
                             .OpenConnectionAsync(CancellationToken.None))
            {
                await using var find = control.CreateCommand();
                find.CommandText = """
                    SELECT pid
                    FROM pg_stat_activity
                    WHERE datname = current_database()
                      AND application_name =
                          'fst-path-generation-admission'
                      AND pid <> pg_backend_pid()
                    ORDER BY backend_start DESC
                    LIMIT 1
                    """;
                var backendPid = Convert.ToInt32(
                    await find.ExecuteScalarAsync(
                        CancellationToken.None));

                await using var terminate = control.CreateCommand();
                terminate.CommandText =
                    "SELECT pg_terminate_backend(@backendPid)";
                terminate.Parameters.AddWithValue(
                    "backendPid",
                    backendPid);
                Assert.True(
                    await terminate.ExecuteScalarAsync(
                        CancellationToken.None) is true);
            }

            await lease.DisposeAsync();
            lease = NoopAsyncDisposable.Instance;

            await using var recoveredLease =
                await provider
                    .AcquireAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await lease.DisposeAsync();
        }
    }

    private static PostgresPathGenerationAdmissionLeaseProvider CreateProvider(
        InMemoryMetaDatabase database)
        => new(
            CreateConnectionString(database),
            NullLogger<PostgresPathGenerationAdmissionLeaseProvider>.Instance);

    private static string CreateConnectionString(
        InMemoryMetaDatabase database)
    {
        var databaseSettings = new NpgsqlConnectionStringBuilder(
            database.DataSource.ConnectionString);
        var connectionString = new NpgsqlConnectionStringBuilder(
            SharedPostgresContainer.ConnectionString)
        {
            Database = databaseSettings.Database,
        };
        return connectionString.ConnectionString;
    }

    private static async Task ObserveWaitersAsync(
        IEnumerable<Task<IAsyncDisposable>> waiters)
    {
        foreach (var waiter in waiters)
        {
            try
            {
                await using var lease = await waiter;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public static NoopAsyncDisposable Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
