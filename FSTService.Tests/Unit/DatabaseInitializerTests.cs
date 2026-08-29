using FortniteFestival.Core.Persistence;
using FortniteFestival.Core.Services;
using FortniteFestival.Core;
using FSTService.Persistence;
using FSTService.Persistence.Maintenance;
using FSTService.Scraping;
using FSTService.Api;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

namespace FSTService.Tests.Unit;

public class DatabaseInitializerTests : IDisposable
{
    private readonly InMemoryMetaDatabase _metaFixture;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly string _tempDir;

    public DatabaseInitializerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"startup_init_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _metaFixture = new InMemoryMetaDatabase();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _persistence = new GlobalLeaderboardPersistence(
            _metaFixture.Db, loggerFactory,
            Substitute.For<ILogger<GlobalLeaderboardPersistence>>(),
            _metaFixture.DataSource,
            Options.Create(new FeatureOptions()));
    }

    public void Dispose()
    {
        _persistence.Dispose();
        _metaFixture.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void Schema_initialization_creates_publication_singleton_without_scrape()
    {
        using var connection =
            _metaFixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM scrape_publication_state
            WHERE id = TRUE
            """;
        Assert.Equal(
            1L,
            Convert.ToInt64(command.ExecuteScalar()));
    }

    [Fact]
    public async Task EnsureSchemaAsync_creates_idempotent_leaderboard_population_guard()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _metaFixture.DataSource);
        await DatabaseInitializer.EnsureSchemaAsync(
            _metaFixture.DataSource);

        using var connection =
            _metaFixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_trigger
                WHERE tgrelid =
                        'leaderboard_population'::regclass
                  AND tgname =
                        'trg_leaderboard_population_registration_mutation_guard'
                  AND NOT tgisinternal
                  AND (tgtype & 1) = 0
                  AND tgfoid =
                        'fst_assert_registration_mutation_allowed()'::regprocedure
            )
            """;

        Assert.True(command.ExecuteScalar() is true);
    }

    [Fact]
    public async Task CheckHealthAsync_BeforeInit_ReturnsUnhealthy()
    {
        var festivalService = new FestivalService((IFestivalPersistence?)null);
        var handler = new HttpClient(new NoOpHandler());
        var shopService = new ItemShopService(handler, festivalService, _metaFixture.Db,
            Substitute.For<ILogger<ItemShopService>>());
        var lifetime = Substitute.For<IHostApplicationLifetime>();

        var init = new StartupInitializer(
            _persistence, _metaFixture.DataSource, festivalService, shopService, lifetime,
            Options.Create(new ScraperOptions { DataDirectory = _tempDir }),
            Substitute.For<ILogger<StartupInitializer>>());

        Assert.False(init.IsReady);
        var result = await init.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_ReadOnlyViolation_ReturnsUnhealthy()
    {
        var festivalService = new FestivalService((IFestivalPersistence?)null);
        var shopService = new ItemShopService(
            new HttpClient(new NoOpHandler()),
            festivalService,
            _metaFixture.Db,
            Substitute.For<ILogger<ItemShopService>>());
        var violations = new RolloutReadOnlyViolationMonitor();
        violations.Report(new InvalidOperationException("injected read-only violation"));
        var initializer = new StartupInitializer(
            _persistence,
            _metaFixture.DataSource,
            festivalService,
            shopService,
            Substitute.For<IHostApplicationLifetime>(),
            Options.Create(new ScraperOptions
            {
                RolloutReadOnlyStartup = true,
                RolloutPostgresReadOnly = true,
            }),
            Substitute.For<ILogger<StartupInitializer>>(),
            violations);

        var result = await initializer.CheckHealthAsync(
            new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("read-only violation", result.Description);
    }

    [Fact]
    public async Task StartAsync_NormalMode_QueriesWritablePostgresAndSignalsReady()
    {
        var festivalService = new FestivalService((IFestivalPersistence?)null);
        var handler = new HttpClient(new NoOpHandler());
        var shopService = new ItemShopService(handler, festivalService, _metaFixture.Db,
            Substitute.For<ILogger<ItemShopService>>());
        var lifetime = Substitute.For<IHostApplicationLifetime>();

        var init = new StartupInitializer(
            _persistence, _metaFixture.DataSource, festivalService, shopService, lifetime,
            Options.Create(new ScraperOptions { DataDirectory = _tempDir }),
            Substitute.For<ILogger<StartupInitializer>>());

        await init.StartAsync(CancellationToken.None);

        // Wait for background init
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await init.WaitForReadyAsync(cts.Token);

        Assert.True(init.IsReady);
        Assert.False(init.PostgresDefaultTransactionReadOnly);
        var result = await init.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task StartAsync_InvalidPublishedScopeBindingFailsClosedBeforeReady()
    {
        var scrapeId = PublishReadyScopeSource();
        using (var connection =
               _metaFixture.DataSource.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DELETE FROM leaderboard_published_scope_source
                WHERE published_scrape_id = @scrapeId
                """;
            command.Parameters.AddWithValue("scrapeId", scrapeId);
            command.ExecuteNonQuery();
        }

        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>())
            .Returns(Substitute.For<ILogger>());
        using var persistence =
            new GlobalLeaderboardPersistence(
                _metaFixture.Db,
                loggerFactory,
                Substitute.For<
                    ILogger<GlobalLeaderboardPersistence>>(),
                _metaFixture.DataSource,
                Options.Create(new FeatureOptions
                {
                    UsePublishedScopeSources = true,
                }));
        var festivalService =
            new FestivalService((IFestivalPersistence?)null);
        var shopService = new ItemShopService(
            new HttpClient(new NoOpHandler()),
            festivalService,
            _metaFixture.Db,
            Substitute.For<ILogger<ItemShopService>>());
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        var initializer = new StartupInitializer(
            persistence,
            _metaFixture.DataSource,
            festivalService,
            shopService,
            lifetime,
            Options.Create(new ScraperOptions
            {
                DataDirectory = _tempDir,
            }),
            Substitute.For<ILogger<StartupInitializer>>());

        await initializer.StartAsync(CancellationToken.None);
        using var cts =
            new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await Assert.ThrowsAnyAsync<Exception>(
            () => initializer.WaitForReadyAsync(cts.Token));
        Assert.False(initializer.IsReady);
        lifetime.Received(1).StopApplication();
    }

    [Fact]
    public async Task CheckHealthAsync_PublishedScopeBindingLossAfterStartupBecomesUnhealthy()
    {
        var scrapeId = PublishReadyScopeSource();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>())
            .Returns(Substitute.For<ILogger>());
        using var persistence =
            new GlobalLeaderboardPersistence(
                _metaFixture.Db,
                loggerFactory,
                Substitute.For<
                    ILogger<GlobalLeaderboardPersistence>>(),
                _metaFixture.DataSource,
                Options.Create(new FeatureOptions
                {
                    UsePublishedScopeSources = true,
                }));
        var festivalService =
            new FestivalService((IFestivalPersistence?)null);
        var shopService = new ItemShopService(
            new HttpClient(new NoOpHandler()),
            festivalService,
            _metaFixture.Db,
            Substitute.For<ILogger<ItemShopService>>());
        var initializer = new StartupInitializer(
            persistence,
            _metaFixture.DataSource,
            festivalService,
            shopService,
            Substitute.For<IHostApplicationLifetime>(),
            Options.Create(new ScraperOptions
            {
                DataDirectory = _tempDir,
            }),
            Substitute.For<ILogger<StartupInitializer>>());

        await initializer.StartAsync(CancellationToken.None);
        using var cts =
            new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await initializer.WaitForReadyAsync(cts.Token);
        Assert.Equal(
            HealthStatus.Healthy,
            (await initializer.CheckHealthAsync(
                new HealthCheckContext())).Status);

        using (var connection =
               _metaFixture.DataSource.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DELETE FROM leaderboard_published_scope_source
                WHERE published_scrape_id = @scrapeId
                """;
            command.Parameters.AddWithValue("scrapeId", scrapeId);
            command.ExecuteNonQuery();
        }
        await Task.Delay(TimeSpan.FromMilliseconds(1_100));

        var health = await initializer.CheckHealthAsync(
            new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, health.Status);
        Assert.Contains(
            "scope-source",
            health.Description,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartAsync_ReconcilesStalePublicationCommitIntent()
    {
        var publishedScrapeId =
            _metaFixture.Db.StartScrapeRun();
        _metaFixture.Db.CompleteScrapeRun(
            publishedScrapeId,
            1,
            1,
            1,
            1);
        _metaFixture.Db.PublishScrapeRun(
            publishedScrapeId,
            promoteCachedResponses: false);
        var candidateScrapeId =
            _metaFixture.Db.StartScrapeRun();
        var candidatePublicationId =
            _metaFixture.Db.GetPublicationPointerState()
                .WorkingPublicationId!.Value;
        using (var connection =
               _metaFixture.DataSource.OpenConnection())
        using (var stale = connection.CreateCommand())
        {
            stale.CommandText = """
                UPDATE scrape_publication_state
                SET public_reads_frozen = TRUE,
                    public_reads_frozen_at =
                        now() - interval '5 minutes',
                    public_reads_frozen_scrape_id =
                        @scrapeId,
                    public_reads_frozen_reason =
                        @commitIntentReason,
                    updated_at = now()
                WHERE id = TRUE
                """;
            stale.Parameters.AddWithValue(
                "scrapeId",
                checked((int)candidateScrapeId));
            stale.Parameters.AddWithValue(
                "commitIntentReason",
                PublicReadFreezeState
                    .PublicationCommitIntentReason);
            stale.ExecuteNonQuery();
        }
        var orphanBandTable =
            BandRankingStorageNames
                .GetPreparedPublishedRankingTable(
                    999_994,
                    "Band_Duets");
        using (var connection =
               _metaFixture.DataSource.OpenConnection())
        using (var orphan = connection.CreateCommand())
        {
            orphan.CommandText =
                $"CREATE TABLE {BandRankingStorageNames.QuoteIdentifier(orphanBandTable)} (id INTEGER)";
            orphan.ExecuteNonQuery();
        }

        var festivalService =
            new FestivalService((IFestivalPersistence?)null);
        var shopService = new ItemShopService(
            new HttpClient(new NoOpHandler()),
            festivalService,
            _metaFixture.Db,
            Substitute.For<ILogger<ItemShopService>>());
        var initializer = new StartupInitializer(
            _persistence,
            _metaFixture.DataSource,
            festivalService,
            shopService,
            Substitute.For<IHostApplicationLifetime>(),
            Options.Create(new ScraperOptions
            {
                DataDirectory = _tempDir,
            }),
            Substitute.For<ILogger<StartupInitializer>>(),
            publicationCommitOptions:
                Options.Create(new PublicationCommitOptions
                {
                    StaleCommitIntentSeconds = 1,
                }));

        await initializer.StartAsync(CancellationToken.None);
        using var cts =
            new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await initializer.WaitForReadyAsync(cts.Token);

        Assert.False(
            _metaFixture.Db.GetPublicReadFreezeState()
                .IsFrozen);
        Assert.Null(
            _metaFixture.Db.GetPublicationPointerState()
                .WorkingPublicationId);
        Assert.Equal(
            PublicationGenerationStatus.Failed,
            _metaFixture.Db.GetPublicationGeneration(
                candidatePublicationId)?.Status);
        using (var connection =
               _metaFixture.DataSource.OpenConnection())
        using (var exists = connection.CreateCommand())
        {
            exists.CommandText =
                "SELECT to_regclass(@tableName) IS NOT NULL";
            exists.Parameters.AddWithValue(
                "tableName",
                orphanBandTable);
            Assert.False(exists.ExecuteScalar() is true);
        }
    }

    [Fact]
    public async Task StartAsync_ReconcilesAbandonedReadyWorkingGeneration()
    {
        var publishedScrapeId =
            _metaFixture.Db.StartScrapeRun();
        _metaFixture.Db.CompleteScrapeRun(
            publishedScrapeId,
            1,
            1,
            1,
            1);
        _metaFixture.Db.PublishScrapeRun(
            publishedScrapeId,
            promoteCachedResponses: false);
        var abandonedScrapeId =
            _metaFixture.Db.StartScrapeRun();
        _metaFixture.Db.CompleteScrapeRun(
            abandonedScrapeId,
            1,
            2,
            2,
            2);
        var abandoned =
            _metaFixture.Db.PrepareScrapePublication(
                abandonedScrapeId,
                promoteCachedResponses: false);

        var festivalService =
            new FestivalService((IFestivalPersistence?)null);
        var shopService = new ItemShopService(
            new HttpClient(new NoOpHandler()),
            festivalService,
            _metaFixture.Db,
            Substitute.For<ILogger<ItemShopService>>());
        var initializer = new StartupInitializer(
            _persistence,
            _metaFixture.DataSource,
            festivalService,
            shopService,
            Substitute.For<IHostApplicationLifetime>(),
            Options.Create(new ScraperOptions
            {
                DataDirectory = _tempDir,
            }),
            Substitute.For<ILogger<StartupInitializer>>(),
            publicationCommitOptions:
                Options.Create(new PublicationCommitOptions
                {
                    AbandonedReadyGraceSeconds = 1,
                    WorkerHeartbeatFreshSeconds = 1,
                }));

        await initializer.StartAsync(CancellationToken.None);
        using var cts =
            new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await initializer.WaitForReadyAsync(cts.Token);

        Assert.Null(
            _metaFixture.Db.GetPublicationPointerState()
                .WorkingPublicationId);
        Assert.Equal(
            PublicationGenerationStatus.Failed,
            _metaFixture.Db.GetPublicationGeneration(
                abandoned.PublicationId)?.Status);
    }

    [Fact]
    public async Task StartAsync_ClearsPendingIsolationForCurrentPublishedScrape()
    {
        var publishedScrapeId =
                _metaFixture.Db.StartScrapeRun();
        _metaFixture.Db.CompleteScrapeRun(
                publishedScrapeId,
                1,
                1,
                1,
                1);
        _metaFixture.Db.PublishScrapeRun(
                publishedScrapeId,
                promoteCachedResponses: false);
        var publicationId =
                _metaFixture.Db.GetPublicationPointerState()
                    .CurrentPublicationId!.Value;
        _metaFixture.Db.SetPublicReadFreeze(
                true,
                publishedScrapeId,
                PublicReadFreezeState
                    .PublicationFailureIsolationPendingReason);

        var festivalService =
                new FestivalService((IFestivalPersistence?)null);
        var shopService = new ItemShopService(
                new HttpClient(new NoOpHandler()),
                festivalService,
                _metaFixture.Db,
                Substitute.For<ILogger<ItemShopService>>());
        var initializer = new StartupInitializer(
                _persistence,
                _metaFixture.DataSource,
                festivalService,
                shopService,
                Substitute.For<IHostApplicationLifetime>(),
                Options.Create(new ScraperOptions
                {
                    DataDirectory = _tempDir,
                }),
                Substitute.For<ILogger<StartupInitializer>>());

        await initializer.StartAsync(CancellationToken.None);
        using var cts =
                new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await initializer.WaitForReadyAsync(cts.Token);

        Assert.False(
                _metaFixture.Db.GetPublicReadFreezeState().IsFrozen);
        Assert.Equal(
                publicationId,
                _metaFixture.Db.GetPublicationPointerState()
                    .CurrentPublicationId);
        Assert.Equal(
                PublicationGenerationStatus.Current,
                _metaFixture.Db.GetPublicationGeneration(
                    publicationId)?.Status);
    }

    [Fact]
    public async Task StartAsync_RolloutReadOnlyStartup_PerformsNoDatabaseOrFilesystemWrites()
    {
        var song = new Song
        {
            track = new Track
            {
                su = "rollout-read-only-song",
                tt = "Persisted Song",
                an = "Artist",
            },
        };
        var writableFestivalPersistence = new FestivalPersistence(
            _metaFixture.DataSource);
        await writableFestivalPersistence.SaveSongsAsync([song]);
        _metaFixture.Db.SaveItemShopTracks(
            new HashSet<string> { song.track.su },
            new HashSet<string>(),
            new HashSet<string>(),
            DateTime.UtcNow);

        string databaseName;
        using (var connection = _metaFixture.DataSource.OpenConnection())
            databaseName = connection.Database;
        var readOnlyBuilder = new NpgsqlConnectionStringBuilder(
            SharedPostgresContainer.ConnectionString)
        {
            Database = databaseName,
            Options = "-c default_transaction_read_only=on",
            MinPoolSize = 0,
            MaxPoolSize = 5,
        };
        await using var readOnlyDataSource =
            NpgsqlDataSource.Create(readOnlyBuilder.ConnectionString);
        using var readOnlyMeta = new MetaDatabase(
            readOnlyDataSource,
            Substitute.For<ILogger<MetaDatabase>>());
        var readOnlyLoggerFactory = Substitute.For<ILoggerFactory>();
        readOnlyLoggerFactory.CreateLogger(Arg.Any<string>())
            .Returns(Substitute.For<ILogger>());
        using var readOnlyPersistence = new GlobalLeaderboardPersistence(
            readOnlyMeta,
            readOnlyLoggerFactory,
            Substitute.For<ILogger<GlobalLeaderboardPersistence>>(),
            readOnlyDataSource,
            Options.Create(new FeatureOptions()));
        var providerHandler = new CountingFailureHandler();
        var shopHandler = new CountingFailureHandler();
        var festivalService = new FestivalService(
            new FestivalPersistence(readOnlyDataSource),
            new HttpClient(providerHandler));
        var shopService = new ItemShopService(
            new HttpClient(shopHandler),
            festivalService,
            readOnlyMeta,
            Substitute.For<ILogger<ItemShopService>>());
        var lifetime = Substitute.For<IHostApplicationLifetime>();

        var midiDirectory = Path.Combine(_tempDir, "midi");
        Directory.CreateDirectory(midiDirectory);
        var legacyDat = Path.Combine(midiDirectory, "preserve.dat");
        await File.WriteAllTextAsync(legacyDat, "preserve");
        var staleSpool = Path.Combine(_tempDir, "spool", "fst_scrape_preserve");
        Directory.CreateDirectory(staleSpool);
        Directory.SetLastWriteTimeUtc(staleSpool, DateTime.UtcNow.AddDays(-7));

        var initializer = new StartupInitializer(
            readOnlyPersistence,
            readOnlyDataSource,
            festivalService,
            shopService,
            lifetime,
            Options.Create(new ScraperOptions
            {
                DataDirectory = _tempDir,
                RolloutReadOnlyStartup = true,
                RolloutPostgresReadOnly = true,
            }),
            Substitute.For<ILogger<StartupInitializer>>());

        await initializer.StartAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await initializer.WaitForReadyAsync(cts.Token);

        Assert.True(initializer.IsReady);
        Assert.True(initializer.PostgresDefaultTransactionReadOnly);
        Assert.Contains(
            festivalService.Songs,
            loaded => loaded.track.su == song.track.su);
        Assert.Contains(song.track.su, shopService.InShopSongIds);
        Assert.False(shopService.HasScheduledRefresh);
        Assert.Equal(0, providerHandler.RequestCount);
        Assert.Equal(0, shopHandler.RequestCount);
        Assert.True(File.Exists(legacyDat));
        Assert.True(Directory.Exists(staleSpool));
        lifetime.DidNotReceive().StopApplication();

        using var secondPersistence = new GlobalLeaderboardPersistence(
            readOnlyMeta,
            readOnlyLoggerFactory,
            Substitute.For<ILogger<GlobalLeaderboardPersistence>>(),
            readOnlyDataSource,
            Options.Create(new FeatureOptions()));
        var secondProviderHandler = new CountingFailureHandler();
        var secondShopHandler = new CountingFailureHandler();
        var secondFestivalService = new FestivalService(
            new FestivalPersistence(readOnlyDataSource),
            new HttpClient(secondProviderHandler));
        var secondShopService = new ItemShopService(
            new HttpClient(secondShopHandler),
            secondFestivalService,
            readOnlyMeta,
            Substitute.For<ILogger<ItemShopService>>());
        var secondInitializer = new StartupInitializer(
            secondPersistence,
            readOnlyDataSource,
            secondFestivalService,
            secondShopService,
            lifetime,
            Options.Create(new ScraperOptions
            {
                DataDirectory = _tempDir,
                RolloutReadOnlyStartup = true,
                RolloutPostgresReadOnly = true,
            }),
            Substitute.For<ILogger<StartupInitializer>>());

        await secondInitializer.StartAsync(CancellationToken.None);
        await secondInitializer.WaitForReadyAsync(cts.Token);

        Assert.Equal(0, secondProviderHandler.RequestCount);
        Assert.Equal(0, secondShopHandler.RequestCount);
        Assert.False(secondShopService.HasScheduledRefresh);
        Assert.True(secondInitializer.PostgresDefaultTransactionReadOnly);
        Assert.True(File.Exists(legacyDat));
        Assert.True(Directory.Exists(staleSpool));
    }

    [Fact]
    public async Task StartAsync_RolloutReadOnlyStartup_RejectsUnreleasedPublicationPathArtifacts()
    {
        var song = new Song
        {
            track = new Track
            {
                su = "rollout-path-release-song",
                tt = "Persisted Song",
                an = "Artist",
            },
        };
        var writableFestivalPersistence = new FestivalPersistence(
            _metaFixture.DataSource);
        await writableFestivalPersistence.SaveSongsAsync([song]);
        var scrapeId = _metaFixture.Db.StartScrapeRun();
        _metaFixture.Db.CompleteScrapeRun(
            scrapeId,
            1,
            1,
            1,
            1);
        _metaFixture.Db.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false);

        using (var connection =
               _metaFixture.DataSource.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE publication_surface_bindings
                SET binding_json =
                        jsonb_set(
                            binding_json,
                            '{manifestVersion}',
                            '1'::jsonb),
                    content_hash = repeat('0', 64)
                WHERE publication_id = (
                        SELECT current_publication_id
                        FROM scrape_publication_state
                        WHERE id = TRUE)
                  AND surface_name = 'path_artifacts'
                """;
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        string databaseName;
        using (var connection =
               _metaFixture.DataSource.OpenConnection())
        {
            databaseName = connection.Database;
        }
        var readOnlyBuilder = new NpgsqlConnectionStringBuilder(
            SharedPostgresContainer.ConnectionString)
        {
            Database = databaseName,
            Options = "-c default_transaction_read_only=on",
            MinPoolSize = 0,
            MaxPoolSize = 5,
        };
        await using var readOnlyDataSource =
            NpgsqlDataSource.Create(
                readOnlyBuilder.ConnectionString);
        using var readOnlyMeta = new MetaDatabase(
            readOnlyDataSource,
            Substitute.For<ILogger<MetaDatabase>>());
        var readOnlyLoggerFactory =
            Substitute.For<ILoggerFactory>();
        readOnlyLoggerFactory
            .CreateLogger(Arg.Any<string>())
            .Returns(Substitute.For<ILogger>());
        using var readOnlyPersistence =
            new GlobalLeaderboardPersistence(
                readOnlyMeta,
                readOnlyLoggerFactory,
                Substitute.For<
                    ILogger<GlobalLeaderboardPersistence>>(),
                readOnlyDataSource,
                Options.Create(new FeatureOptions()));
        var festivalService = new FestivalService(
            new FestivalPersistence(readOnlyDataSource),
            new HttpClient(new NoOpHandler()));
        var shopService = new ItemShopService(
            new HttpClient(new NoOpHandler()),
            festivalService,
            readOnlyMeta,
            Substitute.For<ILogger<ItemShopService>>());
        var lifetime =
            Substitute.For<IHostApplicationLifetime>();
        var initializer = new StartupInitializer(
            readOnlyPersistence,
            readOnlyDataSource,
            festivalService,
            shopService,
            lifetime,
            Options.Create(new ScraperOptions
            {
                DataDirectory = _tempDir,
                RolloutReadOnlyStartup = true,
                RolloutPostgresReadOnly = true,
                UsePublicationPathArtifacts = true,
            }),
            Substitute.For<ILogger<StartupInitializer>>());

        await initializer.StartAsync(CancellationToken.None);
        using var cts =
            new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<
            PublicationPathArtifactReleaseException>(
            () => initializer.WaitForReadyAsync(cts.Token));

        Assert.False(initializer.IsReady);
        Assert.True(
            initializer.PostgresDefaultTransactionReadOnly);
        lifetime.Received(1).StopApplication();
    }

    [Fact]
    public async Task StartAsync_RolloutReadOnlyStartup_RejectsServerWritablePostgresSession()
    {
        var festivalService = new FestivalService((IFestivalPersistence?)null);
        var shopService = new ItemShopService(
            new HttpClient(new NoOpHandler()),
            festivalService,
            _metaFixture.Db,
            Substitute.For<ILogger<ItemShopService>>());
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        var initializer = new StartupInitializer(
            _persistence,
            _metaFixture.DataSource,
            festivalService,
            shopService,
            lifetime,
            Options.Create(new ScraperOptions
            {
                DataDirectory = _tempDir,
                RolloutReadOnlyStartup = true,
                RolloutPostgresReadOnly = true,
            }),
            Substitute.For<ILogger<StartupInitializer>>());

        await initializer.StartAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => initializer.WaitForReadyAsync(cts.Token));

        Assert.False(initializer.PostgresDefaultTransactionReadOnly);
        lifetime.Received(1).StopApplication();
    }

    [Fact]
    public async Task StartAsync_NormalMode_RejectsRoleLevelReadOnlyPostgresSession()
    {
        string databaseName;
        using (var connection = _metaFixture.DataSource.OpenConnection())
            databaseName = connection.Database;
        var roleName = $"startup_read_only_{Guid.NewGuid():N}";
        const string password = "startup-read-only-test";
        using (var connection = _metaFixture.DataSource.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                CREATE ROLE "{roleName}" LOGIN PASSWORD '{password}';
                GRANT CONNECT ON DATABASE "{databaseName}" TO "{roleName}";
                ALTER ROLE "{roleName}" SET default_transaction_read_only = on;
                """;
            command.ExecuteNonQuery();
        }

        NpgsqlDataSource? roleDataSource = null;
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(
                _metaFixture.DataSource.ConnectionString)
            {
                Database = databaseName,
                Username = roleName,
                Password = password,
                MinPoolSize = 0,
                MaxPoolSize = 2,
            };
            roleDataSource = NpgsqlDataSource.Create(builder.ConnectionString);
            var festivalService = new FestivalService((IFestivalPersistence?)null);
            var shopService = new ItemShopService(
                new HttpClient(new NoOpHandler()),
                festivalService,
                _metaFixture.Db,
                Substitute.For<ILogger<ItemShopService>>());
            var lifetime = Substitute.For<IHostApplicationLifetime>();
            var initializer = new StartupInitializer(
                _persistence,
                roleDataSource,
                festivalService,
                shopService,
                lifetime,
                Options.Create(new ScraperOptions
                {
                    DataDirectory = _tempDir,
                }),
                Substitute.For<ILogger<StartupInitializer>>());

            await initializer.StartAsync(CancellationToken.None);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => initializer.WaitForReadyAsync(cts.Token));

            Assert.Contains(
                "Normal startup requires default_transaction_read_only=off",
                error.Message);
            Assert.True(initializer.PostgresDefaultTransactionReadOnly);
            lifetime.Received(1).StopApplication();
        }
        finally
        {
            if (roleDataSource is not null)
                await roleDataSource.DisposeAsync();
            using var connection = _metaFixture.DataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                DROP OWNED BY "{roleName}";
                DROP ROLE "{roleName}";
                """;
            command.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task StopAsync_ReturnsCompletedTask()
    {
        var festivalService = new FestivalService((IFestivalPersistence?)null);
        var handler = new HttpClient(new NoOpHandler());
        var shopService = new ItemShopService(handler, festivalService, _metaFixture.Db,
            Substitute.For<ILogger<ItemShopService>>());
        var lifetime = Substitute.For<IHostApplicationLifetime>();

        var init = new StartupInitializer(
            _persistence, _metaFixture.DataSource, festivalService, shopService, lifetime,
            Options.Create(new ScraperOptions { DataDirectory = _tempDir }),
            Substitute.For<ILogger<StartupInitializer>>());

        await init.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EnsureSchemaAsync_creates_idempotent_published_scope_source_schema()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                to_regclass('public.leaderboard_published_scope_source') IS NOT NULL,
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'leaderboard_scope_fingerprints'
                      AND column_name = 'is_complete'
                      AND is_nullable = 'NO'
                ),
                (
                    SELECT array_agg(attribute.attname ORDER BY key.ordinality)
                    FROM pg_constraint constraint_row
                    CROSS JOIN LATERAL unnest(constraint_row.conkey) WITH ORDINALITY AS key(attnum, ordinality)
                    JOIN pg_attribute attribute
                      ON attribute.attrelid = constraint_row.conrelid
                     AND attribute.attnum = key.attnum
                    WHERE constraint_row.conrelid = 'leaderboard_published_scope_source'::regclass
                      AND constraint_row.contype = 'p'
                )
            """;
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Equal(
            new[] { "published_scrape_id", "instrument", "song_id", "scope_kind" },
            reader.GetFieldValue<string[]>(2));
    }

    [Fact]
    public async Task EnsureSchemaAsync_creates_idempotent_path_generation_atomicity_schema()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                to_regclass('public.path_generation_errors') IS NOT NULL,
                COUNT(*) = 6,
                EXISTS (
                    SELECT 1
                    FROM pg_trigger
                    WHERE tgname = 'trg_reject_incoherent_legacy_path_write'
                      AND NOT tgisinternal)
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'songs'
              AND column_name = ANY(ARRAY[
                  'chopt_binary_sha256',
                  'path_generation_profile',
                  'path_artifact_generation_id',
                  'path_expected_instruments',
                  'path_generation_revision',
                  'path_generation_pending'
              ])
            """;
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
    }

    [Fact]
    public void SchemaInitializationPlanSeparatesShortNotificationMigration()
    {
        var plan = DatabaseInitializer.GetSchemaInitializationPlan();

        Assert.Equal(
            new[]
            {
                "improvement-notifications",
                "score-history-dedup-audit",
                "main-publication",
                "publication-generation-retirement-columns",
                "publication-generation-foreign-keys",
                "publication-generation-retirement-index",
                "publication-path-artifacts",
                "snapshot-generation-retention-report-only",
                "max-score-maintenance",
            },
            plan.Select(static step => step.Name));
        var notification = plan[0];
        Assert.True(notification.UseShortTransaction);
        Assert.Equal(20, notification.CommandTimeoutSeconds);
        Assert.Equal("2s", notification.LockTimeout);
        Assert.Equal("15s", notification.StatementTimeout);
        Assert.Contains(ImprovementNotificationSchema.Sql, notification.Sql);
        var scoreHistoryAudit = plan[1];
        Assert.True(scoreHistoryAudit.UseShortTransaction);
        Assert.Equal(20, scoreHistoryAudit.CommandTimeoutSeconds);
        Assert.Equal("2s", scoreHistoryAudit.LockTimeout);
        Assert.Equal("15s", scoreHistoryAudit.StatementTimeout);
        Assert.Equal(
            ScoreHistoryDedupMaintenanceSchema.Sql,
            scoreHistoryAudit.Sql);
        Assert.False(plan[2].UseShortTransaction);
        Assert.DoesNotContain(
            "DROP CONSTRAINT publication_generations_scrape_id_fkey",
            plan[2].Sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ADD COLUMN IF NOT EXISTS retired_at",
            plan[2].Sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ADD COLUMN IF NOT EXISTS retired_scrape_id",
            plan[2].Sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ix_publication_generations_retired_scrape",
            plan[2].Sql,
            StringComparison.Ordinal);
        var retirementColumns = plan[3];
        Assert.True(retirementColumns.UseShortTransaction);
        Assert.Equal(20, retirementColumns.CommandTimeoutSeconds);
        Assert.Equal("2s", retirementColumns.LockTimeout);
        Assert.Equal("15s", retirementColumns.StatementTimeout);
        Assert.Contains(
            "ADD COLUMN IF NOT EXISTS retired_at",
            retirementColumns.Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "ADD COLUMN IF NOT EXISTS retired_scrape_id",
            retirementColumns.Sql,
            StringComparison.Ordinal);
        var publicationForeignKeys = plan[4];
        Assert.True(publicationForeignKeys.UseShortTransaction);
        Assert.Equal(20, publicationForeignKeys.CommandTimeoutSeconds);
        Assert.Equal("2s", publicationForeignKeys.LockTimeout);
        Assert.Equal("15s", publicationForeignKeys.StatementTimeout);
        Assert.Contains(
            "publication_generations_scrape_id_restrict_fkey_v2",
            publicationForeignKeys.Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "ON DELETE RESTRICT",
            publicationForeignKeys.Sql,
            StringComparison.Ordinal);
        var retirementIndex = plan[5];
        Assert.False(retirementIndex.UseShortTransaction);
        Assert.True(retirementIndex.UseConcurrentIndex);
        Assert.Equal(20, retirementIndex.CommandTimeoutSeconds);
        Assert.Equal("2s", retirementIndex.LockTimeout);
        Assert.Equal("15s", retirementIndex.StatementTimeout);
        Assert.Contains(
            "CREATE INDEX CONCURRENTLY",
            retirementIndex.Sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IF NOT EXISTS",
            retirementIndex.Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "ix_publication_generations_retired_scrape",
            retirementIndex.Sql,
            StringComparison.Ordinal);
        Assert.Equal(
            PublicationGenerationRetirementSchemaMigration
                .IndexValidationSql,
            retirementIndex.ValidationSql);
        Assert.Equal(
            PublicationGenerationRetirementSchemaMigration
                .DropIndexSql,
            retirementIndex.CleanupSql);
        var pathArtifacts = plan[6];
        Assert.True(pathArtifacts.UseShortTransaction);
        Assert.Equal(20, pathArtifacts.CommandTimeoutSeconds);
        Assert.Equal("2s", pathArtifacts.LockTimeout);
        Assert.Equal("15s", pathArtifacts.StatementTimeout);
        Assert.Equal(
            PublicationPathArtifactSchema.Sql,
            pathArtifacts.Sql);
        var retention = plan[7];
        Assert.True(retention.UseShortTransaction);
        Assert.Equal(
            SnapshotGenerationRetentionSchema.Sql,
            retention.Sql);
        var maxScoreMaintenance = plan[8];
        Assert.True(maxScoreMaintenance.UseShortTransaction);
        Assert.Equal(20, maxScoreMaintenance.CommandTimeoutSeconds);
        Assert.Equal("2s", maxScoreMaintenance.LockTimeout);
        Assert.Equal("15s", maxScoreMaintenance.StatementTimeout);
        Assert.Equal(
            MaxScoreMaintenanceSchema.Sql,
            maxScoreMaintenance.Sql);
    }

    [Fact]
    public async Task PublicationGenerationForeignKeyMigrationAddsRollingSafeRestrict()
    {
        using (var connection =
               _metaFixture.DataSource.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                ALTER TABLE publication_generations
                    DROP CONSTRAINT IF EXISTS
                        publication_generations_scrape_id_restrict_fkey_v2
                """;
            command.ExecuteNonQuery();
        }

        await DatabaseInitializer
            .EnsurePublicationGenerationForeignKeysAsync(
                _metaFixture.DataSource);

        using var verifyConnection =
            _metaFixture.DataSource.OpenConnection();
        using var verify = verifyConnection.CreateCommand();
        verify.CommandText = """
            SELECT
                (
                    SELECT confdeltype::TEXT
                    FROM pg_constraint
                    WHERE conrelid =
                            'publication_generations'::regclass
                      AND conname =
                            'publication_generations_scrape_id_fkey'
                ),
                (
                    SELECT confdeltype::TEXT
                    FROM pg_constraint
                    WHERE conrelid =
                            'publication_generations'::regclass
                      AND conname =
                            'publication_generations_scrape_id_restrict_fkey_v2'
                      AND convalidated
                )
            """;
        using var reader = verify.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("c", reader.GetString(0));
        Assert.Equal("r", reader.GetString(1));
    }

    [Fact]
    public async Task PublicationGenerationForeignKeyMigrationIsNoOpWhenAlreadyExact()
    {
        long beforeOid;
        using (var connection =
               _metaFixture.DataSource.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT oid::BIGINT
                FROM pg_constraint
                WHERE conrelid =
                        'publication_generations'::regclass
                  AND conname =
                        'publication_generations_scrape_id_restrict_fkey_v2'
                """;
            beforeOid = (long)command.ExecuteScalar()!;
        }

        await DatabaseInitializer
            .EnsurePublicationGenerationForeignKeysAsync(
                _metaFixture.DataSource);

        using var verifyConnection =
            _metaFixture.DataSource.OpenConnection();
        using var verify = verifyConnection.CreateCommand();
        verify.CommandText = """
            SELECT oid::BIGINT
            FROM pg_constraint
            WHERE conrelid =
                    'publication_generations'::regclass
              AND conname =
                    'publication_generations_scrape_id_restrict_fkey_v2'
            """;
        Assert.Equal(
            beforeOid,
            (long)verify.ExecuteScalar()!);
    }

    [Fact]
    public async Task PublicationGenerationForeignKeyMigrationLockTimeoutRollsBackAndRetries()
    {
        using (var connection =
               _metaFixture.DataSource.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                ALTER TABLE publication_generations
                    DROP CONSTRAINT IF EXISTS
                        publication_generations_scrape_id_restrict_fkey_v2
                """;
            command.ExecuteNonQuery();
        }

        await using var holder =
            await _metaFixture.DataSource.OpenConnectionAsync();
        await using var holderTransaction =
            await holder.BeginTransactionAsync();
        await using (var lockCommand = holder.CreateCommand())
        {
            lockCommand.Transaction = holderTransaction;
            lockCommand.CommandText = """
                LOCK TABLE publication_generations
                IN ACCESS EXCLUSIVE MODE
                """;
            await lockCommand.ExecuteNonQueryAsync();
        }

        var timeout = await Assert.ThrowsAsync<PostgresException>(
            () => DatabaseInitializer
                .EnsurePublicationGenerationForeignKeysAsync(
                    _metaFixture.DataSource));
        Assert.Equal(
            PostgresErrorCodes.LockNotAvailable,
            timeout.SqlState);
        await using (var verifyRollback = holder.CreateCommand())
        {
            verifyRollback.Transaction = holderTransaction;
            verifyRollback.CommandText = """
                SELECT COUNT(*)
                FROM pg_constraint
                WHERE conrelid =
                        'publication_generations'::regclass
                  AND conname =
                        'publication_generations_scrape_id_restrict_fkey_v2'
                """;
            Assert.Equal(
                0L,
                Convert.ToInt64(
                    await verifyRollback
                        .ExecuteScalarAsync()));
        }
        await holderTransaction.RollbackAsync();

        await DatabaseInitializer
            .EnsurePublicationGenerationForeignKeysAsync(
                _metaFixture.DataSource);

        await using var verifyConnection =
            await _metaFixture.DataSource.OpenConnectionAsync();
        await using var verify = verifyConnection.CreateCommand();
        verify.CommandText = """
            SELECT confdeltype::TEXT
            FROM pg_constraint
            WHERE conrelid =
                    'publication_generations'::regclass
              AND conname =
                    'publication_generations_scrape_id_restrict_fkey_v2'
            """;
        Assert.Equal(
            "r",
            await verify.ExecuteScalarAsync());
    }

    [Fact]
    public async Task PublicationGenerationProtectionSurvivesLegacyInitializerRewrite()
    {
        using (var connection =
               _metaFixture.DataSource.OpenConnection())
        using (var priorService = connection.CreateCommand())
        {
            priorService.CommandText = """
                ALTER TABLE publication_generations
                    DROP CONSTRAINT
                        publication_generations_scrape_id_fkey;
                ALTER TABLE publication_generations
                    ADD CONSTRAINT
                        publication_generations_scrape_id_fkey
                    FOREIGN KEY (scrape_id)
                    REFERENCES scrape_log(id)
                    ON DELETE RESTRICT;
                """;
            priorService.ExecuteNonQuery();
        }

        await DatabaseInitializer
            .EnsurePublicationGenerationForeignKeysAsync(
                _metaFixture.DataSource);

        using (var connection =
               _metaFixture.DataSource.OpenConnection())
        using (var legacyInitializer =
               connection.CreateCommand())
        {
            legacyInitializer.CommandText = """
                DO $legacy_initializer$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM pg_constraint
                        WHERE conrelid =
                                'publication_generations'
                                    ::regclass
                          AND conname =
                                'publication_generations_scrape_id_fkey'
                          AND confdeltype <> 'c'
                    ) THEN
                        ALTER TABLE publication_generations
                            DROP CONSTRAINT
                                publication_generations_scrape_id_fkey;
                        ALTER TABLE publication_generations
                            ADD CONSTRAINT
                                publication_generations_scrape_id_fkey
                            FOREIGN KEY (scrape_id)
                            REFERENCES scrape_log(id)
                            ON DELETE CASCADE;
                    END IF;
                END
                $legacy_initializer$;
                """;
            legacyInitializer.ExecuteNonQuery();
        }

        using (var connection =
               _metaFixture.DataSource.OpenConnection())
        using (var verify = connection.CreateCommand())
        {
            verify.CommandText = """
                SELECT
                    (
                        SELECT confdeltype = 'c'
                        FROM pg_constraint constraint_row
                        WHERE constraint_row.conrelid =
                                'publication_generations'
                                    ::regclass
                          AND constraint_row.conname =
                                'publication_generations_scrape_id_fkey'
                    ),
                    EXISTS (
                        SELECT 1
                        FROM pg_constraint constraint_row
                        WHERE constraint_row.conrelid =
                                'publication_generations'
                                    ::regclass
                          AND constraint_row.conname =
                                'publication_generations_scrape_id_restrict_fkey_v2'
                          AND constraint_row.confdeltype = 'r'
                          AND constraint_row.convalidated),
                    EXISTS (
                        SELECT 1
                        FROM pg_trigger trigger_row
                        WHERE trigger_row.tgrelid =
                                'scrape_log'::regclass
                          AND trigger_row.tgname =
                                'trg_scrape_log_restrict_publication_generation_delete_v2'
                          AND NOT trigger_row.tgisinternal
                          AND trigger_row.tgenabled = 'O')
                """;
            using var reader = verify.ExecuteReader();
            Assert.True(reader.Read());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
        }

        var scrapeId = _metaFixture.Db.StartScrapeRun();

        using (var connection =
               _metaFixture.DataSource.OpenConnection())
        using (var delete = connection.CreateCommand())
        {
            delete.CommandText = """
                DELETE FROM scrape_log
                WHERE id = @scrapeId
                """;
            delete.Parameters.AddWithValue(
                "scrapeId",
                scrapeId);
            var error =
                Assert.Throws<PostgresException>(
                    () => delete.ExecuteNonQuery());
            Assert.Equal(
                PostgresErrorCodes.ForeignKeyViolation,
                error.SqlState);
        }

        using (var connection =
               _metaFixture.DataSource.OpenConnection())
        using (var verify = connection.CreateCommand())
        {
            verify.CommandText = """
                SELECT COUNT(*)
                FROM publication_generations
                WHERE scrape_id = @scrapeId
                """;
            verify.Parameters.AddWithValue(
                "scrapeId",
                scrapeId);
            Assert.Equal(
                1L,
                Convert.ToInt64(
                    verify.ExecuteScalar()));
        }
    }

    [Fact]
    public async Task PublicationGenerationRetirementMigrationIsIdempotent()
    {
        long beforeIndexOid;
        using (var connection =
               _metaFixture.DataSource.OpenConnection())
        using (var before = connection.CreateCommand())
        {
            before.CommandText = """
                SELECT oid::BIGINT
                FROM pg_class
                WHERE oid =
                    'public.ix_publication_generations_retired_scrape'
                        ::regclass
                """;
            beforeIndexOid =
                (long)before.ExecuteScalar()!;
        }

        await DatabaseInitializer
            .EnsurePublicationGenerationRetirementSchemaAsync(
                _metaFixture.DataSource);
        await DatabaseInitializer
            .EnsurePublicationGenerationRetirementSchemaAsync(
                _metaFixture.DataSource);

        using var verifyConnection =
            _metaFixture.DataSource.OpenConnection();
        using var verify =
            verifyConnection.CreateCommand();
        verify.CommandText = $"""
            SELECT
                (
                    SELECT COUNT(*)
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name =
                            'publication_generations'
                      AND column_name IN (
                            'retired_at',
                            'retired_scrape_id')
                ),
                (
                    SELECT oid::BIGINT
                    FROM pg_class
                    WHERE oid =
                        'public.ix_publication_generations_retired_scrape'
                            ::regclass
                ),
                ({PublicationGenerationRetirementSchemaMigration.IndexValidationSql})
            """;
        using var reader = verify.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(2, reader.GetInt64(0));
        Assert.Equal(
            beforeIndexOid,
            reader.GetInt64(1));
        Assert.True(reader.GetBoolean(2));
    }

    [Fact]
    public async Task PublicationGenerationRetirementColumnLockTimeoutRollsBackAndRetries()
    {
        using (var connection =
               _metaFixture.DataSource.OpenConnection())
        using (var drop = connection.CreateCommand())
        {
            drop.CommandText = """
                DROP INDEX CONCURRENTLY
                    public.ix_publication_generations_retired_scrape
                """;
            drop.ExecuteNonQuery();
            drop.CommandText = """
                ALTER TABLE publication_generations
                    DROP COLUMN retired_at,
                    DROP COLUMN retired_scrape_id
                """;
            drop.ExecuteNonQuery();
        }

        await using var holder =
            await _metaFixture.DataSource.OpenConnectionAsync();
        await using var holderTransaction =
            await holder.BeginTransactionAsync();
        await using (var hold = holder.CreateCommand())
        {
            hold.Transaction = holderTransaction;
            hold.CommandText = """
                LOCK TABLE publication_generations
                IN ACCESS SHARE MODE
                """;
            await hold.ExecuteNonQueryAsync();
        }

        var timeout = await Assert.ThrowsAsync<PostgresException>(
            () => DatabaseInitializer
                .EnsurePublicationGenerationRetirementSchemaAsync(
                    _metaFixture.DataSource));
        Assert.Equal(
            PostgresErrorCodes.LockNotAvailable,
            timeout.SqlState);
        await using (var verifyRollback =
                     holder.CreateCommand())
        {
            verifyRollback.Transaction =
                holderTransaction;
            verifyRollback.CommandText = """
                SELECT COUNT(*)
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name =
                        'publication_generations'
                  AND column_name IN (
                        'retired_at',
                        'retired_scrape_id')
                """;
            Assert.Equal(
                0L,
                Convert.ToInt64(
                    await verifyRollback
                        .ExecuteScalarAsync()));
        }
        await holderTransaction.RollbackAsync();

        await DatabaseInitializer
            .EnsurePublicationGenerationRetirementSchemaAsync(
                _metaFixture.DataSource);

        await using var verifyConnection =
            await _metaFixture.DataSource.OpenConnectionAsync();
        await using var verify =
            verifyConnection.CreateCommand();
        verify.CommandText = $"""
            SELECT
                (
                    SELECT COUNT(*)
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name =
                            'publication_generations'
                      AND column_name IN (
                            'retired_at',
                            'retired_scrape_id')
                ) = 2
                AND
                ({PublicationGenerationRetirementSchemaMigration.IndexValidationSql})
            """;
        Assert.True(
            (bool)(await verify
                .ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task PublicationGenerationRetirementIndexLockTimeoutIsRetryable()
    {
        using (var connection =
               _metaFixture.DataSource.OpenConnection())
        using (var drop = connection.CreateCommand())
        {
            drop.CommandText =
                PublicationGenerationRetirementSchemaMigration
                    .DropIndexSql;
            drop.ExecuteNonQuery();
        }

        await using var holder =
            await _metaFixture.DataSource.OpenConnectionAsync();
        await using var holderTransaction =
            await holder.BeginTransactionAsync();
        await using (var hold = holder.CreateCommand())
        {
            hold.Transaction = holderTransaction;
            hold.CommandText = """
                LOCK TABLE publication_generations
                IN SHARE UPDATE EXCLUSIVE MODE
                """;
            await hold.ExecuteNonQueryAsync();
        }

        var timeout = await Assert.ThrowsAsync<PostgresException>(
            () => DatabaseInitializer
                .EnsurePublicationGenerationRetirementIndexAsync(
                    _metaFixture.DataSource));
        Assert.Equal(
            PostgresErrorCodes.LockNotAvailable,
            timeout.SqlState);
        await holderTransaction.RollbackAsync();

        await DatabaseInitializer
            .EnsurePublicationGenerationRetirementIndexAsync(
                _metaFixture.DataSource);

        await using var verifyConnection =
            await _metaFixture.DataSource.OpenConnectionAsync();
        await using var verify =
            verifyConnection.CreateCommand();
        verify.CommandText =
            PublicationGenerationRetirementSchemaMigration
                .IndexValidationSql;
        Assert.True(
            (bool)(await verify
                .ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task PublicationGenerationRetirementIndexRepairsInvalidRetryArtifact()
    {
        long invalidIndexOid;
        using (var connection =
               _metaFixture.DataSource.OpenConnection())
        using (var corrupt = connection.CreateCommand())
        {
            corrupt.CommandText = """
                UPDATE pg_index
                SET indisvalid = FALSE,
                    indisready = FALSE
                WHERE indexrelid =
                    'public.ix_publication_generations_retired_scrape'
                        ::regclass
                RETURNING indexrelid::BIGINT
                """;
            invalidIndexOid =
                (long)corrupt.ExecuteScalar()!;
        }

        await DatabaseInitializer
            .EnsurePublicationGenerationRetirementIndexAsync(
                _metaFixture.DataSource);

        using var verifyConnection =
            _metaFixture.DataSource.OpenConnection();
        using var verify =
            verifyConnection.CreateCommand();
        verify.CommandText = $"""
            SELECT
                index_relation.oid::BIGINT,
                ({PublicationGenerationRetirementSchemaMigration.IndexValidationSql})
            FROM pg_class index_relation
            WHERE index_relation.oid =
                'public.ix_publication_generations_retired_scrape'
                    ::regclass
            """;
        using var reader = verify.ExecuteReader();
        Assert.True(reader.Read());
        Assert.NotEqual(
            invalidIndexOid,
            reader.GetInt64(0));
        Assert.True(reader.GetBoolean(1));
    }

    [Fact]
    public async Task EnsureSchemaAsync_DoesNotCreateRetiredAggregateRankingDeltaRelations()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM pg_class relation
            JOIN pg_namespace schema_row
              ON schema_row.oid = relation.relnamespace
            WHERE schema_row.nspname = 'public'
              AND relation.relkind IN ('r', 'p')
              AND (
                    relation.relname = 'ranking_deltas'
                 OR relation.relname LIKE 'ranking_deltas_%'
                 OR relation.relname = 'ranking_delta_tiers'
                 OR relation.relname LIKE 'ranking_delta_tiers_%'
                 OR relation.relname = 'rank_history_deltas'
                 OR relation.relname LIKE 'rank_history_deltas_%'
                 OR relation.relname = 'composite_ranking_deltas'
                 OR relation.relname = 'combo_ranking_deltas'
              )
            """;

        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task EnsureSchemaAsync_creates_immutable_score_history_dedup_audit_schema()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using (var inspect = conn.CreateCommand())
        {
            inspect.CommandText = """
                SELECT
                    to_regclass(
                        'public.score_history_dedup_maintenance_runs')
                        IS NOT NULL,
                    to_regclass(
                        'public.score_history_dedup_original_rows')
                        IS NOT NULL,
                    COUNT(*) FILTER (
                        WHERE trigger_row.tgname =
                            'trg_reject_score_history_dedup_run_mutation'
                          AND NOT trigger_row.tgisinternal) = 1,
                    COUNT(*) FILTER (
                        WHERE trigger_row.tgname =
                            'trg_reject_score_history_dedup_original_mutation'
                          AND NOT trigger_row.tgisinternal) = 1,
                    NOT EXISTS (
                        SELECT 1
                        FROM pg_constraint constraint_row
                        WHERE constraint_row.conrelid =
                            'score_history_dedup_maintenance_runs'::regclass
                          AND constraint_row.contype = 'f')
                FROM pg_trigger trigger_row
                WHERE trigger_row.tgrelid IN (
                    'score_history_dedup_maintenance_runs'::regclass,
                    'score_history_dedup_original_rows'::regclass);
                """;
            using var reader = inspect.ExecuteReader();
            Assert.True(reader.Read());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
            Assert.True(reader.GetBoolean(3));
            Assert.True(reader.GetBoolean(4));
        }

        using (var index = conn.CreateCommand())
        {
            index.CommandText = """
                SELECT indisunique, indisvalid, indnullsnotdistinct
                FROM pg_index
                WHERE indexrelid = 'public.ix_sh_dedup'::regclass;
                """;
            using var reader = index.ExecuteReader();
            Assert.True(reader.Read());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
        }

        using (var insert = conn.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO score_history_dedup_maintenance_runs (
                    maintenance_purpose,
                    maintenance_contract_version,
                    execution_source,
                    dry_run_digest,
                    canonical_candidate_data,
                    safety_classification,
                    database_name,
                    database_user,
                    server_version_num,
                    duplicate_row_count,
                    duplicate_group_count,
                    excess_row_count,
                    affected_account_count,
                    affected_song_count,
                    original_rows_audited,
                    survivor_rows_updated,
                    rows_deleted,
                    index_replaced,
                    index_definition_before,
                    index_definition_after,
                    rollback_sql)
                VALUES (
                    'score_history_null_timestamp_dedup_v1',
                    1,
                    'explicit_cli',
                    @digest,
                    '{}',
                    'ready',
                    current_database(),
                    current_user,
                    current_setting('server_version_num')::INTEGER,
                    0, 0, 0, 0, 0, 0, 0, 0,
                    TRUE,
                    'legacy',
                    'null-safe',
                    'rollback')
                RETURNING maintenance_run_id;
                """;
            insert.Parameters.AddWithValue("digest", new string('a', 64));
            var runId = Convert.ToInt64(insert.ExecuteScalar());

            using var mutate = conn.CreateCommand();
            mutate.CommandText = """
                UPDATE score_history_dedup_maintenance_runs
                SET rollback_sql = 'changed'
                WHERE maintenance_run_id = @runId;
                """;
            mutate.Parameters.AddWithValue("runId", runId);
            var exception = Assert.Throws<PostgresException>(
                () => mutate.ExecuteNonQuery());
            Assert.Equal("55000", exception.SqlState);
        }
    }

    [Fact]
    public async Task EnsureSchemaAsync_migrates_score_history_dedup_contract_v1()
    {
        using (var conn = _metaFixture.DataSource.OpenConnection())
        using (var downgrade = conn.CreateCommand())
        {
            downgrade.CommandText = """
                ALTER TABLE score_history_dedup_maintenance_runs
                    DROP CONSTRAINT
                        ck_score_history_dedup_contract_version;
                ALTER TABLE score_history_dedup_maintenance_runs
                    ADD CONSTRAINT
                        ck_score_history_dedup_contract_version_v1
                    CHECK (maintenance_contract_version = 1);
                """;
            downgrade.ExecuteNonQuery();
        }

        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var inspectConn = _metaFixture.DataSource.OpenConnection();
        using var inspect = inspectConn.CreateCommand();
        inspect.CommandText = """
            SELECT
                COUNT(*) FILTER (
                    WHERE pg_get_constraintdef(
                        constraint_row.oid,
                        TRUE) =
                        'CHECK (maintenance_contract_version = ANY (ARRAY[1, 2]))'
                ),
                COUNT(*) FILTER (
                    WHERE pg_get_constraintdef(
                        constraint_row.oid,
                        TRUE) =
                        'CHECK (maintenance_contract_version = 1)'
                ),
                COUNT(*)
            FROM pg_constraint constraint_row
            WHERE constraint_row.conrelid =
                    'score_history_dedup_maintenance_runs'::regclass
              AND constraint_row.contype = 'c'
              AND pg_get_constraintdef(
                    constraint_row.oid,
                    TRUE) LIKE
                    'CHECK (maintenance_contract_version%';
            """;
        using var reader = inspect.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
    }

    [Fact]
    public async Task MaintenanceAuditPublishedScrapeProvenanceIsDurableAndImmutable()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        int scrapeId;
        using (var insertScrape = conn.CreateCommand())
        {
            insertScrape.CommandText = """
                INSERT INTO scrape_log (
                    started_at, completed_at, status,
                    songs_scraped, total_entries, total_requests, total_bytes)
                VALUES (
                    now(), now(), 'completed',
                    0, 0, 0, 0)
                RETURNING id;
                """;
            scrapeId = (int)insertScrape.ExecuteScalar()!;
        }

        using (var insertRun = conn.CreateCommand())
        {
            insertRun.CommandText = """
                INSERT INTO improvement_notification_maintenance_runs (
                    notification_purpose,
                    notification_cause,
                    delivery_state,
                    published_scrape_id,
                    dry_run_digest,
                    canonical_candidate_data,
                    repair_manifest,
                    total_charted_songs)
                VALUES (
                    'maintenance_pro_lead_max_score_repair_v1',
                    'max_score_recompute',
                    'quarantined',
                    @scrapeId,
                    @digest,
                    '{}',
                    '{"manifestVersion":1,"songs":[]}'::jsonb,
                    4);
                """;
            insertRun.Parameters.AddWithValue("scrapeId", scrapeId);
            insertRun.Parameters.AddWithValue("digest", new string('a', 64));
            Assert.Equal(1, insertRun.ExecuteNonQuery());
        }

        using (var deleteScrape = conn.CreateCommand())
        {
            deleteScrape.CommandText = "DELETE FROM scrape_log WHERE id = @scrapeId;";
            deleteScrape.Parameters.AddWithValue("scrapeId", scrapeId);
            Assert.Equal(1, deleteScrape.ExecuteNonQuery());
        }

        using (var inspect = conn.CreateCommand())
        {
            inspect.CommandText = """
                SELECT run.published_scrape_id,
                       column_row.is_nullable = 'NO',
                       NOT EXISTS (
                           SELECT 1
                           FROM pg_constraint constraint_row
                           JOIN pg_attribute attribute
                             ON attribute.attrelid = constraint_row.conrelid
                            AND attribute.attnum = ANY(constraint_row.conkey)
                           WHERE constraint_row.conrelid =
                               'improvement_notification_maintenance_runs'::regclass
                             AND constraint_row.contype = 'f'
                             AND attribute.attname = 'published_scrape_id'
                       )
                FROM improvement_notification_maintenance_runs run
                CROSS JOIN information_schema.columns column_row
                WHERE column_row.table_schema = 'public'
                  AND column_row.table_name =
                      'improvement_notification_maintenance_runs'
                  AND column_row.column_name = 'published_scrape_id';
                """;
            using var reader = inspect.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(scrapeId, reader.GetInt32(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
        }

        using var mutate = conn.CreateCommand();
        mutate.CommandText = """
            UPDATE improvement_notification_maintenance_runs
            SET published_scrape_id = @replacement
            WHERE published_scrape_id = @scrapeId;
            """;
        mutate.Parameters.AddWithValue("replacement", scrapeId + 1);
        mutate.Parameters.AddWithValue("scrapeId", scrapeId);
        var exception = Assert.Throws<PostgresException>(
            () => mutate.ExecuteNonQuery());
        Assert.Equal("55000", exception.SqlState);
    }

    [Fact]
    public async Task EnsureSchemaAsync_creates_idempotent_registered_user_refresh_scope_schema()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                to_regclass('public.registered_user_refresh_scope_progress') IS NOT NULL,
                to_regclass('public.ix_registered_user_refresh_scope_checked_at') IS NOT NULL,
                (
                    SELECT array_agg(attribute.attname ORDER BY key.ordinality)
                    FROM pg_constraint constraint_row
                    CROSS JOIN LATERAL unnest(constraint_row.conkey)
                        WITH ORDINALITY AS key(attnum, ordinality)
                    JOIN pg_attribute attribute
                      ON attribute.attrelid = constraint_row.conrelid
                     AND attribute.attnum = key.attnum
                    WHERE constraint_row.conrelid =
                        'registered_user_refresh_scope_progress'::regclass
                      AND constraint_row.contype = 'p'
                ),
                (
                    SELECT array_agg(column_name ORDER BY ordinal_position)
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'registered_user_refresh_scope_progress'
                      AND is_nullable = 'NO'
                ),
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'registered_user_refresh_scope_progress'
                      AND column_name = 'scrape_id'
                      AND is_nullable = 'YES'
                ),
                EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conname = 'ck_registered_user_refresh_scope_provenance_v2'
                      AND conrelid =
                        'registered_user_refresh_scope_progress'::regclass
                      AND convalidated
                )
            """;

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Equal(new[] { "song_id", "instrument" }, reader.GetFieldValue<string[]>(2));
        Assert.Equal(
            new[] { "song_id", "instrument", "status", "checked_at", "provenance" },
            reader.GetFieldValue<string[]>(3));
        Assert.True(reader.GetBoolean(4));
        Assert.True(reader.GetBoolean(5));
    }

    [Fact]
    public async Task EnsureSchemaAsync_upgrades_registered_user_refresh_scrape_provenance_idempotently()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using (var conn = _metaFixture.DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                ALTER TABLE registered_user_refresh_scope_progress
                    DROP CONSTRAINT ck_registered_user_refresh_scope_provenance_v2;
                ALTER TABLE registered_user_refresh_scope_progress
                    DROP COLUMN provenance;
                ALTER TABLE registered_user_refresh_scope_progress
                    ALTER COLUMN scrape_id SET NOT NULL;

                INSERT INTO registered_user_refresh_scope_progress (
                    song_id,
                    instrument,
                    status,
                    checked_at,
                    scrape_id)
                VALUES (
                    'legacy-refresh-song',
                    'Solo_Guitar',
                    'complete',
                    '2026-08-01T00:00:00Z',
                    1272);
                """;
            cmd.ExecuteNonQuery();
        }

        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var verifyConn = _metaFixture.DataSource.OpenConnection();
        using var verifyCmd = verifyConn.CreateCommand();
        verifyCmd.CommandText = """
            SELECT
                scrape_id,
                provenance,
                (
                    SELECT is_nullable
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'registered_user_refresh_scope_progress'
                      AND column_name = 'scrape_id'
                )
            FROM registered_user_refresh_scope_progress
            WHERE song_id = 'legacy-refresh-song'
              AND instrument = 'Solo_Guitar'
            """;
        using var reader = verifyCmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1272, reader.GetInt64(0));
        Assert.Equal("scrape", reader.GetString(1));
        Assert.Equal("YES", reader.GetString(2));
    }

    [Fact]
    public async Task EnsureSchemaAsync_replaces_prior_provenance_constraint_and_enforces_null_semantics()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using (var conn = _metaFixture.DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                ALTER TABLE registered_user_refresh_scope_progress
                    DROP CONSTRAINT ck_registered_user_refresh_scope_provenance_v2;
                ALTER TABLE registered_user_refresh_scope_progress
                    ADD CONSTRAINT ck_registered_user_refresh_scope_provenance
                    CHECK (
                        (provenance = 'scrape' AND scrape_id > 0)
                        OR (provenance = 'phase_only' AND scrape_id IS NULL));
                """;
            cmd.ExecuteNonQuery();
        }

        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var verifyConn = _metaFixture.DataSource.OpenConnection();
        using (var verifyConstraint = verifyConn.CreateCommand())
        {
            verifyConstraint.CommandText = """
                SELECT
                    NOT EXISTS (
                        SELECT 1
                        FROM pg_constraint
                        WHERE conname = 'ck_registered_user_refresh_scope_provenance'
                          AND conrelid =
                            'registered_user_refresh_scope_progress'::regclass),
                    EXISTS (
                        SELECT 1
                        FROM pg_constraint
                        WHERE conname = 'ck_registered_user_refresh_scope_provenance_v2'
                          AND conrelid =
                            'registered_user_refresh_scope_progress'::regclass
                          AND convalidated)
                """;
            using var reader = verifyConstraint.ExecuteReader();
            Assert.True(reader.Read());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
        }

        using (var invalid = verifyConn.CreateCommand())
        {
            invalid.CommandText = """
                INSERT INTO registered_user_refresh_scope_progress (
                    song_id,
                    instrument,
                    status,
                    checked_at,
                    scrape_id,
                    provenance)
                VALUES (
                    'invalid-null-scrape',
                    'Solo_Guitar',
                    'complete',
                    @now,
                    NULL,
                    'scrape')
                """;
            invalid.Parameters.AddWithValue("now", DateTime.UtcNow);

            var exception = Assert.Throws<PostgresException>(
                () => invalid.ExecuteNonQuery());
            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        }

        using (var valid = verifyConn.CreateCommand())
        {
            valid.CommandText = """
                INSERT INTO registered_user_refresh_scope_progress (
                    song_id,
                    instrument,
                    status,
                    checked_at,
                    scrape_id,
                    provenance)
                VALUES (
                    'valid-null-phase-only',
                    'Solo_Guitar',
                    'complete',
                    @now,
                    NULL,
                    'phase_only')
                """;
            valid.Parameters.AddWithValue("now", DateTime.UtcNow);
            Assert.Equal(1, valid.ExecuteNonQuery());
        }
    }

    [Fact]
    public async Task EnsureSchemaAsync_adds_season_and_history_identity_columns_idempotently()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using (var dropConn = _metaFixture.DataSource.OpenConnection())
        using (var dropCmd = dropConn.CreateCommand())
        {
            dropCmd.CommandText = """
                ALTER TABLE registered_band_processing_progress
                    DROP COLUMN window_id;
                ALTER TABLE registered_player_band_discovery_progress
                    DROP COLUMN window_id;
                ALTER TABLE season_windows
                    DROP COLUMN source_kind;
                ALTER TABLE history_recon_status
                    DROP COLUMN reconstruction_version,
                    DROP COLUMN window_fingerprint,
                    DROP COLUMN admission_revision;
                ALTER TABLE history_recon_progress
                    DROP COLUMN reconstruction_version,
                    DROP COLUMN window_fingerprint,
                    DROP COLUMN admission_revision;
                ALTER TABLE song_first_seen_season
                    DROP COLUMN window_fingerprint,
                    DROP COLUMN max_season;
                """;
            dropCmd.ExecuteNonQuery();
        }

        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'registered_band_processing_progress'
                      AND column_name = 'window_id'
                      AND is_nullable = 'NO'),
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'registered_player_band_discovery_progress'
                      AND column_name = 'window_id'
                      AND is_nullable = 'NO'),
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'season_windows'
                      AND column_name = 'source_kind'
                      AND is_nullable = 'NO'),
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'history_recon_status'
                      AND column_name = 'window_fingerprint'
                      AND is_nullable = 'NO'),
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'history_recon_progress'
                      AND column_name = 'reconstruction_version'
                      AND is_nullable = 'NO'),
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'history_recon_status'
                      AND column_name = 'admission_revision'
                      AND is_nullable = 'NO'),
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'song_first_seen_season'
                      AND column_name = 'window_fingerprint'
                      AND is_nullable = 'NO')
            """;
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
        Assert.True(reader.GetBoolean(5));
        Assert.True(reader.GetBoolean(6));
    }

    [Fact]
    public async Task EnsureSchemaAsync_invalidates_legacy_completed_history_reconstruction()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using (var conn = _metaFixture.DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO history_recon_status (
                    account_id,
                    status,
                    songs_processed,
                    total_songs_to_process,
                    completed_at,
                    reconstruction_version,
                    window_fingerprint)
                VALUES (
                    'legacy-history',
                    'complete',
                    10,
                    10,
                    @now,
                    1,
                    '');
                INSERT INTO history_recon_progress (
                    account_id,
                    song_id,
                    instrument,
                    processed,
                    processed_at,
                    reconstruction_version,
                    window_fingerprint)
                VALUES (
                    'legacy-history',
                    'song-a',
                    'Solo_Guitar',
                    1,
                    @now,
                    1,
                    '');
                """;
            cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
            cmd.ExecuteNonQuery();
        }

        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var verifyConn = _metaFixture.DataSource.OpenConnection();
        using var verifyCmd = verifyConn.CreateCommand();
        verifyCmd.CommandText = """
            SELECT status, songs_processed, completed_at, reconstruction_version
            FROM history_recon_status
            WHERE account_id = 'legacy-history'
            """;
        using var reader = verifyCmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("pending", reader.GetString(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.True(reader.IsDBNull(2));
        Assert.Equal(1, reader.GetInt32(3));
    }

    [Fact]
    public async Task EnsureSchemaAsync_creates_worker_correctness_ledgers()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                to_regclass('public.leaderboard_scope_manifests') IS NOT NULL,
                to_regclass('public.scrape_writer_failures') IS NOT NULL,
                to_regclass('public.scrape_phase_outcomes') IS NOT NULL,
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'scrape_log'
                      AND column_name = 'status'
                      AND is_nullable = 'NO'
                ),
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'scrape_log'
                      AND column_name = 'best_effort_failed_phases'
                      AND data_type = 'ARRAY'
                )
            """;
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
    }

    [Fact]
    public async Task EnsureSchemaAsync_creates_exact_scrape_phase_timings_shape_idempotently()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using (var columns = conn.CreateCommand())
        {
            columns.CommandText = """
                SELECT
                    array_agg(column_name ORDER BY ordinal_position),
                    array_agg(udt_name ORDER BY ordinal_position),
                    array_agg(is_nullable ORDER BY ordinal_position),
                    array_agg(COALESCE(column_default, '') ORDER BY ordinal_position)
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = 'scrape_phase_timings'
                """;
            using var reader = columns.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(
                [
                    "id",
                    "scrape_id",
                    "phase",
                    "subphase",
                    "item_key",
                    "started_at",
                    "completed_at",
                    "duration_ms",
                    "rows_read",
                    "rows_written",
                    "rows_deleted",
                    "scope_count",
                    "success",
                    "error_message",
                ],
                reader.GetFieldValue<string[]>(0));
            Assert.Equal(
                [
                    "int8",
                    "int8",
                    "text",
                    "text",
                    "text",
                    "timestamptz",
                    "timestamptz",
                    "int8",
                    "int8",
                    "int8",
                    "int8",
                    "int8",
                    "bool",
                    "text",
                ],
                reader.GetFieldValue<string[]>(1));
            Assert.Equal(
                [
                    "NO",
                    "NO",
                    "NO",
                    "YES",
                    "YES",
                    "NO",
                    "NO",
                    "NO",
                    "YES",
                    "YES",
                    "YES",
                    "YES",
                    "NO",
                    "YES",
                ],
                reader.GetFieldValue<string[]>(2));

            var defaults = reader.GetFieldValue<string[]>(3);
            Assert.Contains("nextval('scrape_phase_timings_id_seq'::regclass)", defaults[0]);
            Assert.Equal("true", defaults[12]);
        }

        using (var constraints = conn.CreateCommand())
        {
            constraints.CommandText = """
                SELECT
                    count(*) FILTER (WHERE contype = 'p'),
                    count(*) FILTER (WHERE contype = 'f')
                FROM pg_constraint
                WHERE conrelid = 'public.scrape_phase_timings'::regclass
                """;
            using var reader = constraints.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(1, reader.GetInt64(0));
            Assert.Equal(0, reader.GetInt64(1));
        }

        using var indexes = conn.CreateCommand();
        indexes.CommandText = """
            SELECT indexname, indexdef
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename = 'scrape_phase_timings'
            ORDER BY indexname
            """;
        using var indexReader = indexes.ExecuteReader();
        var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
        while (indexReader.Read())
            definitions[indexReader.GetString(0)] = indexReader.GetString(1);

        Assert.Contains("scrape_phase_timings_pkey", definitions.Keys);
        Assert.Contains(
            "(scrape_id, phase, subphase, item_key)",
            definitions["ix_scrape_phase_timings_scrape"]);
        Assert.Contains(
            "(started_at DESC)",
            definitions["ix_scrape_phase_timings_started"]);
    }

    [Fact]
    public void RecordScrapePhaseTiming_roundtrips_after_fresh_schema_bootstrap()
    {
        var startedAt = DateTime.UtcNow.AddSeconds(-2);
        var completedAt = DateTime.UtcNow;
        _metaFixture.Db.RecordScrapePhaseTiming(new ScrapePhaseTimingRecord(
            4242,
            "BandMaintenance",
            "prune",
            null,
            startedAt,
            completedAt,
            2_000,
            RowsRead: 11,
            RowsWritten: 12,
            RowsDeleted: 13,
            ScopeCount: 14));

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT scrape_id, phase, subphase, item_key, duration_ms,
                   rows_read, rows_written, rows_deleted, scope_count,
                   success, error_message
            FROM scrape_phase_timings
            WHERE scrape_id = 4242
            """;
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(4242, reader.GetInt64(0));
        Assert.Equal("BandMaintenance", reader.GetString(1));
        Assert.Equal("prune", reader.GetString(2));
        Assert.True(reader.IsDBNull(3));
        Assert.Equal(2_000, reader.GetInt64(4));
        Assert.Equal(11, reader.GetInt64(5));
        Assert.Equal(12, reader.GetInt64(6));
        Assert.Equal(13, reader.GetInt64(7));
        Assert.Equal(14, reader.GetInt64(8));
        Assert.True(reader.GetBoolean(9));
        Assert.True(reader.IsDBNull(10));
        Assert.False(reader.Read());
    }

    [Fact]
    public async Task EnsureSchemaAsync_creates_exact_scrape_phase_attempts_shape_idempotently()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var columns = conn.CreateCommand();
        columns.CommandText = """
            SELECT
                array_agg(column_name ORDER BY ordinal_position),
                array_agg(udt_name ORDER BY ordinal_position),
                array_agg(is_nullable ORDER BY ordinal_position),
                array_agg(COALESCE(column_default, '') ORDER BY ordinal_position)
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'scrape_phase_attempts'
            """;

        using var reader = columns.ExecuteReader();
        Assert.True(reader.Read());
        var names = reader.GetFieldValue<string[]>(0);
        Assert.Equal(
            [
                "scrape_id",
                "phase_id",
                "attempt",
                "operation_id",
                "phase_ordinal",
                "plan_version",
                "worker_instance_id",
                "current_subphase_id",
                "status",
                "units_kind",
                "units_completed",
                "units_total",
                "units_total_final",
                "phase_percent",
                "overall_percent_kind",
                "overall_percent",
                "overall_model_version",
                "eta_lower_seconds",
                "eta_upper_seconds",
                "eta_confidence",
                "eta_sample_count",
                "current_subphase_epoch",
                "subphase_sequence",
                "subphase_progress_kind",
                "subphase_units_kind",
                "subphase_units_completed",
                "subphase_units_total",
                "subphase_units_total_final",
                "subphase_percent",
                "subphase_started_at",
                "subphase_last_progress_at",
                "started_at",
                "last_progress_at",
                "heartbeat_at",
                "completed_at",
                "build_id",
                "config_id",
                "warning_message",
                "error_message",
            ],
            names);
        Assert.Equal(
            [
                "int8", "text", "int4", "text", "int4", "text", "text",
                "text", "text", "text", "int8", "int8", "bool", "float8",
                "text", "float8", "text", "float8", "float8", "text", "int4",
                "int4", "int8", "text", "text", "int8", "int8", "bool",
                "float8", "timestamptz", "timestamptz",
                "timestamptz", "timestamptz", "timestamptz", "timestamptz",
                "text", "text", "text", "text",
            ],
            reader.GetFieldValue<string[]>(1));
        var nullability = reader.GetFieldValue<string[]>(2);
        Assert.Equal(
            [
                "NO", "NO", "NO", "NO", "NO", "NO", "NO", "YES", "NO",
                "YES", "YES", "YES", "NO", "YES", "NO", "YES", "YES",
                "YES", "YES", "YES", "YES",
                "NO", "NO", "NO", "YES", "YES", "YES", "NO", "YES",
                "YES", "YES",
                "NO", "NO", "NO", "YES", "YES", "YES", "YES", "YES",
            ],
            nullability);
        var defaults = reader.GetFieldValue<string[]>(3);
        Assert.Equal("false", defaults[12]);
        Assert.Contains("indeterminate", defaults[14]);
        Assert.Equal("0", defaults[21]);
        Assert.Equal("0", defaults[22]);
        Assert.Contains("indeterminate", defaults[23]);
        Assert.Equal("false", defaults[27]);

        reader.Close();
        using (var constraints = conn.CreateCommand())
        {
            constraints.CommandText = """
                SELECT
                    count(*) FILTER (WHERE contype = 'p'),
                    count(*) FILTER (WHERE contype = 'f'),
                    count(*) FILTER (WHERE contype = 'c')
                FROM pg_constraint
                WHERE conrelid = 'public.scrape_phase_attempts'::regclass
                """;
            using var constraintReader = constraints.ExecuteReader();
            Assert.True(constraintReader.Read());
            Assert.Equal(1, constraintReader.GetInt64(0));
            Assert.Equal(0, constraintReader.GetInt64(1));
            Assert.True(constraintReader.GetInt64(2) >= 11);
        }
        using var indexes = conn.CreateCommand();
        indexes.CommandText = """
            SELECT indexname, indexdef
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename = 'scrape_phase_attempts'
            ORDER BY indexname
            """;
        using var indexReader = indexes.ExecuteReader();
        var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
        while (indexReader.Read())
            definitions[indexReader.GetString(0)] = indexReader.GetString(1);
        Assert.Contains("scrape_phase_attempts_pkey", definitions.Keys);
        Assert.DoesNotContain("ix_scrape_phase_attempts_active", definitions.Keys);
        Assert.Contains(
            "(scrape_id, last_progress_at DESC, phase_ordinal DESC, attempt DESC)",
            definitions["ix_scrape_phase_attempts_watchdog"]);
        Assert.Contains(
            "WHERE (status = 'running'::text)",
            definitions["ix_scrape_phase_attempts_watchdog"]);
        Assert.Contains("(worker_instance_id, scrape_id)", definitions["ix_scrape_phase_attempts_instance"]);
        Assert.Contains(
            "(phase_id, plan_version, config_id, completed_at DESC)",
            definitions["ix_scrape_phase_attempts_history"]);
    }

    [Fact]
    public void Scrape_phase_attempt_roundtrips_progress_heartbeat_and_completion()
    {
        var scrapeId = _metaFixture.Db.StartScrapeRun();
        var startedAt = DateTime.UtcNow.AddSeconds(-10);
        var attempt = _metaFixture.Db.StartScrapePhaseAttempt(new ScrapePhaseAttemptStart(
            scrapeId,
            "scrape.leaderboards",
            "scrape.update",
            100,
            "fst.scrape-plan.v2",
            "test-instance",
            "fetching_leaderboards",
            "running",
            "leaderboards",
            0,
            10,
            true,
            0,
            "indeterminate",
            null,
            null,
            null,
            null,
            null,
            null,
            startedAt,
            startedAt,
            startedAt,
            "build-test",
            "config-test"));

        Assert.Equal(1, attempt);
        var progressAt = startedAt.AddSeconds(5);
        Assert.True(_metaFixture.Db.UpdateScrapePhaseAttemptProgress(
            new ScrapePhaseAttemptProgress(
                scrapeId,
                "scrape.leaderboards",
                attempt,
                "persisting_scores",
                "leaderboards",
                5,
                10,
                true,
                50,
                "indeterminate",
                null,
                null,
                null,
                null,
                null,
                null,
                progressAt,
                progressAt)));
        var heartbeatAt = progressAt.AddSeconds(2);
        Assert.Equal(
            1,
            _metaFixture.Db.HeartbeatScrapePhaseAttempts(
                scrapeId,
                "test-instance",
                heartbeatAt));

        var runtime = _metaFixture.Db.GetServiceRuntimeState(
            WorkerStatusPublisher.ScraperWorkerKey);
        var current = Assert.IsType<ScrapePhaseAttemptInfo>(runtime.CurrentPhaseAttempt);
        Assert.Equal("persisting_scores", current.CurrentSubphaseId);
        Assert.Equal(5, current.UnitsCompleted);
        Assert.Equal(50, current.PhasePercent);
        Assert.InRange(
            Math.Abs((progressAt - current.LastProgressAtUtc).TotalMilliseconds),
            0,
            0.001);
        Assert.InRange(
            Math.Abs((heartbeatAt - current.HeartbeatAtUtc).TotalMilliseconds),
            0,
            0.001);

        var completedAt = heartbeatAt.AddSeconds(3);
        Assert.True(_metaFixture.Db.CompleteScrapePhaseAttempt(
            new ScrapePhaseAttemptCompletion(
                scrapeId,
                "scrape.leaderboards",
                attempt,
                "completed",
                completedAt,
                completedAt,
                completedAt,
                null,
                null)));
        Assert.Null(_metaFixture.Db.GetServiceRuntimeState(
            WorkerStatusPublisher.ScraperWorkerKey).CurrentPhaseAttempt);
        Assert.Single(_metaFixture.Db.GetSuccessfulPhaseDurationSamples(
            "scrape.leaderboards",
            "fst.scrape-plan.v2",
            "config-test",
            20));
    }

    [Fact]
    public void Scrape_phase_subphase_progress_is_fenced_and_sequence_ordered()
    {
        var scrapeId = _metaFixture.Db.StartScrapeRun();
        var startedAt = DateTime.UtcNow.AddSeconds(-5);
        var attempt = _metaFixture.Db.StartScrapePhaseAttempt(
            new ScrapePhaseAttemptStart(
                scrapeId,
                "scrape.leaderboards",
                "scrape.update",
                100,
                PhaseProgressCatalog.PlanVersion,
                "instance-a",
                "fetching_leaderboards",
                "running",
                "leaderboards",
                null,
                null,
                false,
                null,
                "indeterminate",
                null,
                null,
                null,
                null,
                null,
                null,
                startedAt,
                startedAt,
                startedAt,
                "build-test",
                "config-test",
                CurrentSubphaseEpoch: 1,
                SubphaseStartedAtUtc: startedAt,
                SubphaseLastProgressAtUtc: startedAt));
        var progressedAt = startedAt.AddSeconds(2);

        Assert.True(_metaFixture.Db.UpdateScrapePhaseAttemptProgress(
            new ScrapePhaseAttemptProgress(
                scrapeId,
                "scrape.leaderboards",
                attempt,
                "fetching_leaderboards",
                "leaderboards",
                5,
                10,
                true,
                50,
                "indeterminate",
                null,
                null,
                null,
                null,
                null,
                null,
                progressedAt,
                progressedAt,
                WorkerInstanceId: "instance-a",
                CurrentSubphaseEpoch: 1,
                SubphaseSequence: 1,
                SubphaseProgressKind: "exact",
                SubphaseUnitsKind: "leaderboards",
                SubphaseUnitsCompleted: 5,
                SubphaseUnitsTotal: 10,
                SubphaseUnitsTotalFinal: true,
                SubphasePercent: 50,
                SubphaseStartedAtUtc: startedAt,
                SubphaseLastProgressAtUtc: progressedAt)));

        Assert.False(_metaFixture.Db.UpdateScrapePhaseAttemptProgress(
            new ScrapePhaseAttemptProgress(
                scrapeId,
                "scrape.leaderboards",
                attempt,
                "fetching_leaderboards",
                "leaderboards",
                6,
                10,
                true,
                60,
                "indeterminate",
                null,
                null,
                null,
                null,
                null,
                null,
                progressedAt,
                progressedAt,
                WorkerInstanceId: "instance-a",
                CurrentSubphaseEpoch: 1,
                SubphaseSequence: 1)));

        Assert.False(_metaFixture.Db.UpdateScrapePhaseAttemptProgress(
            new ScrapePhaseAttemptProgress(
                scrapeId,
                "scrape.leaderboards",
                attempt,
                "persisting_scores",
                "leaderboards",
                10,
                10,
                true,
                100,
                "indeterminate",
                null,
                null,
                null,
                null,
                null,
                null,
                progressedAt,
                progressedAt,
                WorkerInstanceId: "instance-b",
                CurrentSubphaseEpoch: 2,
                SubphaseSequence: 2)));

        Assert.True(_metaFixture.Db.UpdateScrapePhaseAttemptProgress(
            new ScrapePhaseAttemptProgress(
                scrapeId,
                "scrape.leaderboards",
                attempt,
                "persisting_scores",
                "leaderboards",
                10,
                10,
                true,
                100,
                "indeterminate",
                null,
                null,
                null,
                null,
                null,
                null,
                progressedAt,
                progressedAt,
                WorkerInstanceId: "instance-a",
                CurrentSubphaseEpoch: 2,
                SubphaseSequence: 2,
                SubphaseProgressKind: "indeterminate",
                SubphaseStartedAtUtc: progressedAt,
                SubphaseLastProgressAtUtc: progressedAt)));

        var current = Assert.IsType<ScrapePhaseAttemptInfo>(
            _metaFixture.Db.GetServiceRuntimeState(
                WorkerStatusPublisher.ScraperWorkerKey)
                .CurrentPhaseAttempt);
        Assert.Equal(2, current.CurrentSubphaseEpoch);
        Assert.Equal(2, current.SubphaseSequence);
        Assert.Equal("indeterminate", current.SubphaseProgressKind);
        Assert.Null(current.SubphasePercent);
    }

    [Fact]
    public void Scrape_phase_attempt_progress_timestamp_remains_monotonic_across_clock_regression()
    {
        var scrapeId = _metaFixture.Db.StartScrapeRun();
        var startedAt = DateTime.UtcNow;
        var attempt = _metaFixture.Db.StartScrapePhaseAttempt(new ScrapePhaseAttemptStart(
            scrapeId,
            "scrape.leaderboards",
            "scrape.update",
            100,
            PhaseProgressCatalog.PlanVersion,
            "clock-regression-test",
            "fetching_leaderboards",
            "running",
            "leaderboards",
            0,
            10,
            true,
            0,
            "indeterminate",
            null,
            null,
            null,
            null,
            null,
            null,
            startedAt,
            startedAt,
            startedAt,
            "build-test",
            "config-test"));

        var regressedAt = startedAt.AddMinutes(-1);
        Assert.True(_metaFixture.Db.UpdateScrapePhaseAttemptProgress(
            new ScrapePhaseAttemptProgress(
                scrapeId,
                "scrape.leaderboards",
                attempt,
                "persisting_scores",
                "leaderboards",
                1,
                10,
                true,
                10,
                "indeterminate",
                null,
                null,
                null,
                null,
                null,
                null,
                regressedAt,
                regressedAt)));

        var afterRegression = Assert.IsType<ScrapePhaseAttemptInfo>(
            _metaFixture.Db.GetServiceRuntimeState(
                WorkerStatusPublisher.ScraperWorkerKey).CurrentPhaseAttempt);
        Assert.InRange(
            Math.Abs((startedAt - afterRegression.LastProgressAtUtc).TotalMilliseconds),
            0,
            0.001);
        Assert.Equal(1, afterRegression.UnitsCompleted);

        var subsequentAt = startedAt.AddSeconds(5);
        Assert.True(_metaFixture.Db.UpdateScrapePhaseAttemptProgress(
            new ScrapePhaseAttemptProgress(
                scrapeId,
                "scrape.leaderboards",
                attempt,
                "persisting_scores",
                "leaderboards",
                2,
                10,
                true,
                20,
                "indeterminate",
                null,
                null,
                null,
                null,
                null,
                null,
                subsequentAt,
                subsequentAt)));

        var afterRecovery = Assert.IsType<ScrapePhaseAttemptInfo>(
            _metaFixture.Db.GetServiceRuntimeState(
                WorkerStatusPublisher.ScraperWorkerKey).CurrentPhaseAttempt);
        Assert.InRange(
            Math.Abs((subsequentAt - afterRecovery.LastProgressAtUtc).TotalMilliseconds),
            0,
            0.001);
        Assert.Equal(2, afterRecovery.UnitsCompleted);
        Assert.True(_metaFixture.Db.CompleteScrapePhaseAttempt(
            new ScrapePhaseAttemptCompletion(
                scrapeId,
                "scrape.leaderboards",
                attempt,
                "completed",
                subsequentAt,
                subsequentAt,
                subsequentAt,
                null,
                null)));
    }

    [Fact]
    public void Service_runtime_selects_lowest_ordinal_parallel_phase()
    {
        var scrapeId = _metaFixture.Db.StartScrapeRun();
        var now = DateTime.UtcNow;

        _metaFixture.Db.StartScrapePhaseAttempt(new ScrapePhaseAttemptStart(
            scrapeId,
            "post.compute_rankings",
            "scrape.update",
            310,
            PhaseProgressCatalog.PlanVersion,
            "parallel-instance",
            "per_instrument_rankings",
            "running",
            "instruments",
            1,
            8,
            true,
            12.5,
            "indeterminate",
            null,
            null,
            null,
            null,
            null,
            null,
            now,
            now,
            now,
            "build-test",
            "config-test"));
        _metaFixture.Db.StartScrapePhaseAttempt(new ScrapePhaseAttemptStart(
            scrapeId,
            "post.first_seen_season",
            "scrape.update",
            210,
            PhaseProgressCatalog.PlanVersion,
            "parallel-instance",
            "enriching_parallel_tail",
            "running",
            "songs",
            2,
            10,
            true,
            20,
            "indeterminate",
            null,
            null,
            null,
            null,
            null,
            null,
            now.AddSeconds(1),
            now.AddSeconds(1),
            now.AddSeconds(1),
            "build-test",
            "config-test"));

        var current = Assert.IsType<ScrapePhaseAttemptInfo>(
            _metaFixture.Db.GetServiceRuntimeState(
                WorkerStatusPublisher.ScraperWorkerKey)
                .CurrentPhaseAttempt);

        Assert.Equal("post.first_seen_season", current.PhaseId);
        Assert.Equal(210, current.PhaseOrdinal);
    }

    [Fact]
    public void Scrape_phase_attempt_restart_marks_orphan_interrupted()
    {
        var scrapeId = _metaFixture.Db.StartScrapeRun();
        var now = DateTime.UtcNow;
        _metaFixture.Db.StartScrapePhaseAttempt(new ScrapePhaseAttemptStart(
            scrapeId,
            "post.band_maintenance",
            "scrape.update",
            300,
            "fst.scrape-plan.v2",
            "old-instance",
            null,
            "running",
            null,
            null,
            null,
            false,
            null,
            "indeterminate",
            null,
            null,
            null,
            null,
            null,
            null,
            now,
            now,
            now,
            "build-test",
            "config-test"));

        Assert.Equal(
            1,
            _metaFixture.Db.InterruptOrphanedScrapePhaseAttempts(
                "new-instance",
                now.AddMinutes(1),
                "worker restarted"));

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT status, completed_at IS NOT NULL, warning_message
            FROM scrape_phase_attempts
            WHERE scrape_id = @scrapeId
              AND phase_id = 'post.band_maintenance'
            """;
        cmd.Parameters.AddWithValue("scrapeId", scrapeId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("interrupted", reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Equal("worker restarted", reader.GetString(2));
    }

    [Fact]
    public void Scrape_phase_attempt_retry_allocates_next_attempt()
    {
        var scrapeId = _metaFixture.Db.StartScrapeRun();
        var now = DateTime.UtcNow;
        ScrapePhaseAttemptStart Start() => new(
            scrapeId,
            "post.checkpoint",
            "scrape.update",
            360,
            PhaseProgressCatalog.PlanVersion,
            "test-instance",
            null,
            "running",
            "steps",
            null,
            null,
            false,
            null,
            "indeterminate",
            null,
            null,
            null,
            null,
            null,
            null,
            now,
            now,
            now,
            "build-test",
            "config-test");

        var first = _metaFixture.Db.StartScrapePhaseAttempt(Start());
        Assert.True(_metaFixture.Db.CompleteScrapePhaseAttempt(
            new ScrapePhaseAttemptCompletion(
                scrapeId,
                "post.checkpoint",
                first,
                "failed",
                now,
                now,
                now,
                "retrying",
                null)));
        var second = _metaFixture.Db.StartScrapePhaseAttempt(Start());

        Assert.Equal(1, first);
        Assert.Equal(2, second);
    }

    [Fact]
    public async Task EnsureSchemaAsync_does_not_recreate_retired_composite_history_latest_index()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT to_regclass('public.ix_crh_latest') IS NULL";

        Assert.True((bool)cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task EnsureSchemaAsync_does_not_recreate_residual_capacity_indexes()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                to_regclass('public.ix_cr_rank') IS NULL,
                to_regclass('public.ix_pso_union_lookup') IS NULL,
                to_regclass('public.ix_pso_band_source') IS NULL,
                to_regclass('public.ix_band_identity_type_appearance') IS NULL,
                to_regclass('public.ix_bstp_type_appearance') IS NULL,
                to_regclass('public.ix_btr_current_duets_team') IS NULL,
                to_regclass('public.ix_btr_current_trios_team') IS NULL,
                to_regclass('public.ix_btr_current_quad_team') IS NULL,
                to_regclass('public.band_team_rankings_published_band_duets_ix_adjusted') IS NULL,
                to_regclass('public.band_team_rankings_published_band_trios_ix_adjusted') IS NULL,
                to_regclass('public.band_team_rankings_published_band_quad_ix_adjusted') IS NULL
            """;

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            Assert.True(reader.GetBoolean(ordinal));
    }

    [Fact]
    public async Task EnsureSchemaAsync_does_not_create_retired_logical_shadow_schema()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                to_regclass('public.leaderboard_current_entries') IS NULL,
                to_regclass('public.leaderboard_entry_versions') IS NULL,
                to_regclass('public.leaderboard_logical_write_metrics') IS NULL,
                to_regclass('public.ix_llwm_scrape') IS NULL,
                to_regclass('public.ix_lce_scope_rank') IS NULL,
                to_regclass('public.ix_lce_last_changed') IS NULL,
                to_regclass('public.ix_lev_open_versions') IS NULL,
                to_regclass('public.ix_lev_from_scrape') IS NULL
            """;

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            Assert.True(reader.GetBoolean(ordinal));
    }

    [Fact]
    public async Task EnsureSchemaAsync_does_not_create_retired_player_score_observation_schema()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                to_regclass('public.player_score_observations') IS NULL,
                to_regclass('public.player_score_observation_union') IS NULL,
                to_regclass('public.player_score_observations_id_seq') IS NULL,
                to_regclass('public.player_score_observations_pkey') IS NULL,
                to_regclass('public.ux_pso_source') IS NULL
            """;

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            Assert.True(reader.GetBoolean(ordinal));
    }

    [Fact]
    public async Task EnsureSchemaAsync_excludes_only_retired_band_song_team_ranking_projection_schema()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                to_regclass('public.band_song_team_rankings') IS NULL,
                to_regclass('public.band_song_team_ranking_state') IS NULL,
                to_regclass('public.band_song_team_rankings_current_band_duets') IS NULL,
                to_regclass('public.band_song_team_rankings_current_band_trios') IS NULL,
                to_regclass('public.band_song_team_rankings_current_band_quad') IS NULL,
                to_regclass('public.band_song_team_rankings_pkey') IS NULL,
                to_regclass('public.band_song_team_ranking_state_pkey') IS NULL,
                to_regclass('public.band_song_team_rankings_current_band_duets_pkey') IS NULL,
                to_regclass('public.band_song_team_rankings_current_band_trios_pkey') IS NULL,
                to_regclass('public.band_song_team_rankings_current_band_quad_pkey') IS NULL,
                to_regclass('public.ix_bstr_team_best') IS NULL,
                to_regclass('public.ix_bstr_team_worst') IS NULL,
                to_regclass('public.band_current_projection_generation_seq') IS NOT NULL,
                to_regclass('public.current_band_leaderboard_entries') IS NOT NULL,
                to_regclass('public.current_band_leaderboard_entries_duets') IS NOT NULL,
                to_regclass('public.current_band_leaderboard_entries_trios') IS NOT NULL,
                to_regclass('public.current_band_leaderboard_entries_quad') IS NOT NULL,
                to_regclass('public.band_current_projection_scope') IS NOT NULL,
                to_regclass('public.band_current_projection_state') IS NOT NULL,
                to_regclass('public.band_team_rankings_current_band_duets') IS NOT NULL,
                to_regclass('public.band_team_rankings_current_band_trios') IS NOT NULL,
                to_regclass('public.band_team_rankings_current_band_quad') IS NOT NULL,
                to_regclass('public.band_team_rankings_published_band_duets') IS NOT NULL,
                to_regclass('public.band_team_rankings_published_band_trios') IS NOT NULL,
                to_regclass('public.band_team_rankings_published_band_quad') IS NOT NULL,
                to_regclass('public.band_team_ranking_stats_current_band_duets') IS NOT NULL,
                to_regclass('public.band_team_ranking_stats_current_band_trios') IS NOT NULL,
                to_regclass('public.band_team_ranking_stats_current_band_quad') IS NOT NULL,
                to_regclass('public.band_team_ranking_stats_published_band_duets') IS NOT NULL,
                to_regclass('public.band_team_ranking_stats_published_band_trios') IS NOT NULL,
                to_regclass('public.band_team_ranking_stats_published_band_quad') IS NOT NULL
            """;

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            Assert.True(reader.GetBoolean(ordinal));
    }

    [Fact]
    public async Task Retired_composite_history_latest_index_maintenance_sql_is_valid()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using (var create = conn.CreateCommand())
        {
            create.CommandText = """
                CREATE INDEX CONCURRENTLY ix_crh_latest
                    ON public.composite_rank_history USING btree (account_id, snapshot_date DESC)
                """;
            create.ExecuteNonQuery();
        }

        using var verify = conn.CreateCommand();
        verify.CommandText = "SELECT pg_get_indexdef('public.ix_crh_latest'::regclass)";

        Assert.Equal(
            "CREATE INDEX ix_crh_latest ON public.composite_rank_history USING btree (account_id, snapshot_date DESC)",
            verify.ExecuteScalar());

        using (var drop = conn.CreateCommand())
        {
            drop.CommandText = "DROP INDEX CONCURRENTLY public.ix_crh_latest";
            drop.ExecuteNonQuery();
        }

        verify.CommandText = "SELECT to_regclass('public.ix_crh_latest') IS NULL";
        Assert.True((bool)verify.ExecuteScalar()!);
    }

    [Fact]
    public async Task EnsureSchemaAsync_does_not_recreate_retired_rank_history_latest_index()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT to_regclass('public.ix_rh_latest') IS NULL";

        Assert.True((bool)cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task Retired_rank_history_latest_index_family_maintenance_sql_is_valid()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        foreach (var statement in new[]
        {
            """
            CREATE INDEX CONCURRENTLY rank_history_solo_guitar_instrument_account_id_snapshot_dat_idx
                ON public.rank_history_solo_guitar USING btree (instrument, account_id, snapshot_date DESC)
            """,
            """
            CREATE INDEX CONCURRENTLY rank_history_solo_bass_instrument_account_id_snapshot_date_idx
                ON public.rank_history_solo_bass USING btree (instrument, account_id, snapshot_date DESC)
            """,
            """
            CREATE INDEX CONCURRENTLY rank_history_solo_drums_instrument_account_id_snapshot_date_idx
                ON public.rank_history_solo_drums USING btree (instrument, account_id, snapshot_date DESC)
            """,
            """
            CREATE INDEX CONCURRENTLY rank_history_solo_vocals_instrument_account_id_snapshot_dat_idx
                ON public.rank_history_solo_vocals USING btree (instrument, account_id, snapshot_date DESC)
            """,
            """
            CREATE INDEX CONCURRENTLY rank_history_pro_guitar_instrument_account_id_snapshot_date_idx
                ON public.rank_history_pro_guitar USING btree (instrument, account_id, snapshot_date DESC)
            """,
            """
            CREATE INDEX CONCURRENTLY rank_history_pro_bass_instrument_account_id_snapshot_date_idx
                ON public.rank_history_pro_bass USING btree (instrument, account_id, snapshot_date DESC)
            """,
            """
            CREATE INDEX CONCURRENTLY rank_history_pro_vocals_instrument_account_id_snapshot_date_idx
                ON public.rank_history_pro_vocals USING btree (instrument, account_id, snapshot_date DESC)
            """,
            """
            CREATE INDEX CONCURRENTLY rank_history_pro_cymbals_instrument_account_id_snapshot_dat_idx
                ON public.rank_history_pro_cymbals USING btree (instrument, account_id, snapshot_date DESC)
            """,
            """
            CREATE INDEX CONCURRENTLY rank_history_pro_drums_instrument_account_id_snapshot_date_idx
                ON public.rank_history_pro_drums USING btree (instrument, account_id, snapshot_date DESC)
            """,
            """
            CREATE INDEX ix_rh_latest
                ON ONLY public.rank_history USING btree (instrument, account_id, snapshot_date DESC)
            """,
            """
            ALTER INDEX public.ix_rh_latest
                ATTACH PARTITION public.rank_history_solo_guitar_instrument_account_id_snapshot_dat_idx
            """,
            """
            ALTER INDEX public.ix_rh_latest
                ATTACH PARTITION public.rank_history_solo_bass_instrument_account_id_snapshot_date_idx
            """,
            """
            ALTER INDEX public.ix_rh_latest
                ATTACH PARTITION public.rank_history_solo_drums_instrument_account_id_snapshot_date_idx
            """,
            """
            ALTER INDEX public.ix_rh_latest
                ATTACH PARTITION public.rank_history_solo_vocals_instrument_account_id_snapshot_dat_idx
            """,
            """
            ALTER INDEX public.ix_rh_latest
                ATTACH PARTITION public.rank_history_pro_guitar_instrument_account_id_snapshot_date_idx
            """,
            """
            ALTER INDEX public.ix_rh_latest
                ATTACH PARTITION public.rank_history_pro_bass_instrument_account_id_snapshot_date_idx
            """,
            """
            ALTER INDEX public.ix_rh_latest
                ATTACH PARTITION public.rank_history_pro_vocals_instrument_account_id_snapshot_date_idx
            """,
            """
            ALTER INDEX public.ix_rh_latest
                ATTACH PARTITION public.rank_history_pro_cymbals_instrument_account_id_snapshot_dat_idx
            """,
            """
            ALTER INDEX public.ix_rh_latest
                ATTACH PARTITION public.rank_history_pro_drums_instrument_account_id_snapshot_date_idx
            """,
        })
        {
            using var maintenance = conn.CreateCommand();
            maintenance.CommandText = statement;
            maintenance.ExecuteNonQuery();
        }

        using (var verify = conn.CreateCommand())
        {
            verify.CommandText = """
                SELECT COUNT(*)
                FROM pg_index
                WHERE (
                    indexrelid = 'public.ix_rh_latest'::regclass
                    OR indexrelid IN (
                        SELECT inhrelid
                        FROM pg_inherits
                        WHERE inhparent = 'public.ix_rh_latest'::regclass)
                )
                  AND indisvalid
                  AND indisready
                """;

            Assert.Equal(10L, (long)verify.ExecuteScalar()!);
        }

        using (var drop = conn.CreateCommand())
        {
            drop.CommandText = "DROP INDEX public.ix_rh_latest";
            drop.ExecuteNonQuery();
        }

        using var removed = conn.CreateCommand();
        removed.CommandText = """
            SELECT
                to_regclass('public.ix_rh_latest') IS NULL,
                to_regclass('public.rank_history_solo_guitar_instrument_account_id_snapshot_dat_idx') IS NULL,
                to_regclass('public.rank_history_pro_bass_instrument_account_id_snapshot_date_idx') IS NULL
            """;
        using var removedReader = removed.ExecuteReader();
        Assert.True(removedReader.Read());
        Assert.True(removedReader.GetBoolean(0));
        Assert.True(removedReader.GetBoolean(1));
        Assert.True(removedReader.GetBoolean(2));
    }

    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

    private long PublishReadyScopeSource()
    {
        var scrapeId = _metaFixture.Db.StartScrapeRun();
        using (var connection =
               _metaFixture.DataSource.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO leaderboard_scope_fingerprints (
                    song_id,
                    instrument,
                    scope_kind,
                    fingerprint_version,
                    source_scrape_id,
                    published_scrape_id,
                    first_seen_scrape_id,
                    last_changed_scrape_id,
                    last_seen_scrape_id,
                    is_complete,
                    entry_count,
                    reported_total_entries,
                    reported_total_pages,
                    content_fingerprint,
                    coverage_fingerprint,
                    changed_at,
                    seen_at)
                VALUES (
                    'startup-readiness',
                    'Solo_Guitar',
                    'alltime',
                    2,
                    @scrapeId,
                    NULL,
                    @scrapeId,
                    @scrapeId,
                    @scrapeId,
                    TRUE,
                    0,
                    0,
                    0,
                    'empty-content',
                    'empty-coverage',
                    now(),
                    now());

                INSERT INTO leaderboard_published_scope_source (
                    published_scrape_id,
                    song_id,
                    instrument,
                    scope_kind,
                    source_kind,
                    source_snapshot_id,
                    source_scrape_id,
                    row_count,
                    content_fingerprint,
                    coverage_fingerprint,
                    reported_total_entries,
                    reported_total_pages,
                    is_complete,
                    created_at,
                    validated_at)
                VALUES (
                    @scrapeId,
                    'startup-readiness',
                    'Solo_Guitar',
                    'alltime',
                    'empty',
                    NULL,
                    @scrapeId,
                    0,
                    'empty-content',
                    'empty-coverage',
                    0,
                    0,
                    TRUE,
                    now(),
                    now());
                """;
            command.Parameters.AddWithValue("scrapeId", scrapeId);
            command.ExecuteNonQuery();
        }
        _metaFixture.Db.CompleteScrapeRun(
            scrapeId,
            songsScraped: 1,
            totalEntries: 0,
            totalRequests: 1,
            totalBytes: 1);
        _metaFixture.Db.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false,
            expectedPublishedScopeCount: 1);
        return scrapeId;
    }

    private sealed class CountingFailureHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new InvalidOperationException("HTTP is forbidden in read-only startup.");
        }
    }
}
