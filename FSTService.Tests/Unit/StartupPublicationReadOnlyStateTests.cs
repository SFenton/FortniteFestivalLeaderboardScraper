using System.Net;
using FSTService.Api;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using FortniteFestival.Core;
using FortniteFestival.Core.Persistence;
using FortniteFestival.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class StartupPublicationReadOnlyStateTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();
    public void Dispose() => _fixture.Dispose();

    [Theory]
    [InlineData("inexact")]
    [InlineData("missing")]
    [InlineData("working")]
    public async Task Invalid_mutation_pointer_selects_read_only_before_pools_and_keeps_reads_serving(string defect)
    {
        var publication = Publish();
        var target = publication;
        if (defect == "working")
        {
            var scrape = _fixture.Db.StartScrapeRun();
            target = _fixture.Db.GetPublicationGenerationForScrape(scrape)!.PublicationId;
        }
        Execute("""
            UPDATE publication_song_catalog SET is_exact=FALSE
            WHERE publication_id=@target AND @defect='inexact';
            DELETE FROM publication_song_catalog
            WHERE publication_id=@target AND @defect='missing';
            UPDATE publication_surface_bindings
            SET binding_json=jsonb_set(binding_json,'{manifestVersion}','3'::jsonb)
            WHERE publication_id=@target AND surface_name='path_artifacts' AND @defect='working';
            """, command =>
        {
            command.Parameters.AddWithValue("target", target);
            command.Parameters.AddWithValue("defect", defect);
        });
        var before = Binding(target);
        var options = new ScraperOptions { UsePublicationPathArtifacts = true };
        await using var state = await StartupPublicationReadOnlyState.PrepareRuntimeAsync(
            _fixture.DataSource.ConnectionString, options, NullLogger.Instance);
        Assert.True(state.IsLatched);
        Assert.Equal("publication_path_artifact_validation_failed", state.Reason);
        Assert.Contains(state.Failures, failure => failure.PublicationId == target);
        Assert.Equal(before, Binding(target));

        await VerifyServingAsync(state, options, expectReadOnly: true);

        Assert.Equal(before, Binding(target));
        Assert.True(state.IsLatched);
        state.MarkReady();
        Assert.False(state.MutationsReady);
    }

    [Fact]
    public async Task Invalid_previous_is_a_structured_warning_and_normal_startup_succeeds()
    {
        var previous = Publish();
        var current = Publish();
        Execute("""
            UPDATE publication_surface_bindings
            SET binding_json=jsonb_set(binding_json,'{manifestVersion}','3'::jsonb)
            WHERE publication_id=@target AND surface_name='path_artifacts'
            """, command => command.Parameters.AddWithValue("target", previous));
        var before = Binding(previous);
        var log = Substitute.For<ILogger>();
        var options = new ScraperOptions();
        await using var state = await StartupPublicationReadOnlyState.PrepareRuntimeAsync(
            _fixture.DataSource.ConnectionString, options, log);
        Assert.False(state.IsLatched);
        Assert.Contains(state.Warnings, warning => warning.PublicationId == previous
            && warning.Code == "manifest_version_future");
        Assert.Contains(log.ReceivedCalls(), call => call.GetMethodInfo().Name == "Log"
            && call.GetArguments()[0] is LogLevel.Warning
            && call.GetArguments()[2] is IEnumerable<KeyValuePair<string, object?>> fields
            && fields.Any(field => field.Key == "PublicationId" && Equals(field.Value, previous))
            && fields.Any(field => field.Key == "Code" && Equals(field.Value, "manifest_version_future")));

        await VerifyServingAsync(state, options, expectReadOnly: false);

        Assert.True(state.MutationsReady);
        Assert.Equal(before, Binding(previous));
        Assert.Equal(current, _fixture.Db.GetPublicationPointerState().CurrentPublicationId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Skipped_schema_requires_ready_current_and_working_bindings(bool working, bool missing)
    {
        var target = Publish();
        if (working)
        {
            var scrape = _fixture.Db.StartScrapeRun();
            target = _fixture.Db.GetPublicationGenerationForScrape(scrape)!.PublicationId;
        }
        Execute(missing
            ? "DELETE FROM publication_surface_bindings WHERE publication_id=@id AND surface_name='path_artifacts'"
            : "UPDATE publication_surface_bindings SET status='building' WHERE publication_id=@id AND surface_name='path_artifacts'",
            command => command.Parameters.AddWithValue("id", target));
        var before = Binding(target);

        await using var state = await StartupPublicationReadOnlyState.PrepareRuntimeAsync(
            _fixture.DataSource.ConnectionString, new ScraperOptions { ApiOnly = true }, NullLogger.Instance);

        Assert.True(state.IsLatched);
        Assert.Contains(state.Failures, failure => failure.PublicationId == target
            && failure.Code == (missing ? "binding_missing" : "binding_not_ready"));
        Assert.Equal(before, Binding(target));
    }

    [Fact]
    public async Task Failed_persisted_load_retries_without_poisoning_readiness_or_enabling_writes()
    {
        Publish();
        var persisted = Substitute.For<IFestivalPersistence>();
        persisted.LoadScoresAsync().Returns(Task.FromResult<IList<LeaderboardData>>([]));
        persisted.LoadSongsAsync().Returns(
            Task.FromException<IList<Song>>(new NpgsqlException("Injected transient persisted-load failure")),
            Task.FromResult<IList<Song>>([new Song { track = new Track { su = "retry-persisted-song" } }]));
        await using var state = StartupPublicationReadOnlyState.ForInitializedDatabase(readOnly: true);

        var festival = await VerifyServingAsync(state, new ScraperOptions(), expectReadOnly: true, persisted);

        Assert.Single(festival.Songs);
        await persisted.Received(2).LoadScoresAsync();
        await persisted.Received(2).LoadSongsAsync();
        await persisted.DidNotReceive().SaveSongsAsync(Arg.Any<IEnumerable<Song>>());
        await persisted.DidNotReceive().SaveScoresAsync(Arg.Any<IEnumerable<LeaderboardData>>());
    }

    [Fact]
    public async Task Valid_selection_fences_catalog_until_runtime_pool_policy_is_fixed()
    {
        Publish();
        await using var state = await StartupPublicationReadOnlyState.PrepareRuntimeAsync(
            _fixture.DataSource.ConnectionString, new ScraperOptions(), NullLogger.Instance);
        using var connection = _fixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SET lock_timeout='200ms';
            UPDATE publication_surface_bindings SET built_at=built_at
            WHERE surface_name='path_artifacts';
            """;
        var blocked = Assert.Throws<PostgresException>(() => command.ExecuteNonQuery());
        Assert.Equal("55P03", blocked.SqlState);
        await using var runtime = StartupPublicationReadOnlyState.CreateDataSource(
            _fixture.DataSource.ConnectionString, state);
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task Repairing_database_state_cannot_enable_an_existing_read_only_process()
    {
        var publication = Publish();
        Execute("UPDATE publication_song_catalog SET is_exact=FALSE WHERE publication_id=@id",
            command => command.Parameters.AddWithValue("id", publication));
        var options = new ScraperOptions();
        await using var oldState = await StartupPublicationReadOnlyState.PrepareRuntimeAsync(
            _fixture.DataSource.ConnectionString, options, NullLogger.Instance);
        await using var oldSource = StartupPublicationReadOnlyState.CreateDataSource(
            _fixture.DataSource.ConnectionString, oldState);
        Execute("UPDATE publication_song_catalog SET is_exact=TRUE WHERE publication_id=@id",
            command => command.Parameters.AddWithValue("id", publication));
        oldState.MarkReady();
        Assert.False(oldState.MutationsReady);
        using (var connection = oldSource.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SHOW default_transaction_read_only";
            Assert.Equal("on", command.ExecuteScalar());
        }

        await using var restarted = await StartupPublicationReadOnlyState.PrepareRuntimeAsync(
            _fixture.DataSource.ConnectionString, options, NullLogger.Instance);
        await using var newSource = StartupPublicationReadOnlyState.CreateDataSource(
            _fixture.DataSource.ConnectionString, restarted);
        restarted.MarkReady();
        Assert.True(restarted.MutationsReady);
        Assert.True(oldState.IsLatched);
    }

    [Fact]
    public async Task Latched_hosted_services_are_not_constructed_started_or_stopped()
    {
        var state = StartupPublicationReadOnlyState.ForInitializedDatabase(readOnly: true);
        var calls = new HostedCalls();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(state);
        services.AddSingleton(calls);
        services.AddPublicationStartupGatedHostedService<UnexpectedHostedWriter>();
        await using var provider = services.BuildServiceProvider();
        var service = Assert.Single(provider.GetServices<IHostedService>());
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(0, calls.Constructed);
        Assert.Equal(0, calls.Started);
        Assert.Equal(0, calls.Stopped);
    }

    [Fact]
    public async Task Lost_selection_fence_cannot_publish_a_write_capable_pool()
    {
        Publish();
        await using var state = await StartupPublicationReadOnlyState.PrepareRuntimeAsync(
            _fixture.DataSource.ConnectionString, new ScraperOptions(), NullLogger.Instance);
        Assert.False(state.IsLatched);
        await state.DisposeAsync();

        await using var source = StartupPublicationReadOnlyState.CreateDataSource(
            _fixture.DataSource.ConnectionString, state);

        Assert.True(state.IsLatched);
        Assert.Equal("startup_selection_fence_lost", state.Reason);
        using var connection = source.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SHOW default_transaction_read_only";
        Assert.Equal("on", command.ExecuteScalar());
    }

    [Fact]
    public async Task Hosted_writer_selection_seals_pool_policy_before_constructing_a_writer()
    {
        Publish();
        await using var state = await StartupPublicationReadOnlyState.PrepareRuntimeAsync(
            _fixture.DataSource.ConnectionString, new ScraperOptions(), NullLogger.Instance);
        await state.DisposeAsync();
        var calls = new HostedCalls();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(state);
        services.AddSingleton(calls);
        services.AddSingleton(provider => StartupPublicationReadOnlyState.CreateDataSource(
            _fixture.DataSource.ConnectionString, provider.GetRequiredService<StartupPublicationReadOnlyState>()));
        services.AddPublicationStartupGatedHostedService<UnexpectedHostedWriter>();
        await using var provider = services.BuildServiceProvider();

        var service = Assert.Single(provider.GetServices<IHostedService>());
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal("startup_selection_fence_lost", state.Reason);
        Assert.Equal(0, calls.Constructed);
        Assert.Equal(0, calls.Started);
        Assert.Equal(0, calls.Stopped);
    }

    [Fact]
    public async Task Eager_pool_selection_releases_before_a_pipeline_delay_longer_than_idle_timeout()
    {
        Publish();
        var pipelineObserved = false;
        await using var factory = new PoolSelectionFactory(_fixture.DataSource.ConnectionString, services =>
        {
            var selected = services.GetRequiredService<StartupPublicationReadOnlyState>();
            Assert.False(selected.IsLatched);
            Execute("""
                SET lock_timeout='2s';
                UPDATE publication_surface_bindings SET built_at=built_at WHERE surface_name='path_artifacts';
                """, _ => { });
            Thread.Sleep(TimeSpan.FromSeconds(11));
            pipelineObserved = true;
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/version");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(pipelineObserved);
        var runtime = factory.Services.GetRequiredService<NpgsqlDataSource>();
        var state = factory.Services.GetRequiredService<StartupPublicationReadOnlyState>();
        using var connection = runtime.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT current_setting('default_transaction_read_only')='off'
              AND NOT EXISTS (
                  SELECT 1 FROM pg_stat_activity
                  WHERE datname=current_database() AND application_name='fst-startup-schema-selection');
            """;
        Assert.Equal(true, command.ExecuteScalar());
        Assert.False(state.IsLatched);
        Assert.Null(state.Reason);
    }

    [Fact]
    public async Task Concurrent_publication_writer_refuses_selection_within_the_existing_lock_budget()
    {
        Publish();
        using var writer = _fixture.DataSource.OpenConnection();
        using var transaction = writer.BeginTransaction();
        using (var command = writer.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "LOCK TABLE public.publication_path_artifacts IN ROW EXCLUSIVE MODE";
            command.ExecuteNonQuery();
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var state = await StartupPublicationReadOnlyState.PrepareRuntimeAsync(
            _fixture.DataSource.ConnectionString, new ScraperOptions { ApiOnly = true },
            NullLogger.Instance, deadline.Token);
        await using var source = StartupPublicationReadOnlyState.CreateDataSource(
            _fixture.DataSource.ConnectionString, state);
        Assert.True(state.IsLatched);
        Assert.Equal("startup_database_refused_55P03", state.Reason);
        using var connection = source.OpenConnection();
        using var readOnly = connection.CreateCommand();
        readOnly.CommandText = "SHOW default_transaction_read_only";
        Assert.Equal("on", readOnly.ExecuteScalar());
        transaction.Rollback();
    }

    [Fact]
    public void Publication_state_does_not_claim_the_execution_foundation_type_name()
    {
        Assert.Null(typeof(StartupPublicationReadOnlyState).Assembly.GetType("FSTService.StartupReadOnlyState"));
    }

    private async Task<FestivalService> VerifyServingAsync(
        StartupPublicationReadOnlyState state, ScraperOptions options, bool expectReadOnly,
        IFestivalPersistence? persistedOverride = null)
    {
        await using var source = StartupPublicationReadOnlyState.CreateDataSource(
            _fixture.DataSource.ConnectionString, state);
        using var meta = new MetaDatabase(source, NullLogger<MetaDatabase>.Instance);
        using var loggerFactory = LoggerFactory.Create(_ => { });
        using var persistence = new GlobalLeaderboardPersistence(meta, loggerFactory,
            NullLogger<GlobalLeaderboardPersistence>.Instance, source, Options.Create(new FeatureOptions()));
        var http = new CountingHandler();
        var festival = expectReadOnly
            ? new FestivalService(persistedOverride ?? new FestivalPersistence(source), new HttpClient(http))
            : FestivalService.CreateFromSongCatalogSnapshot([]);
        var shop = new ItemShopService(new HttpClient(http), festival, meta,
            NullLogger<ItemShopService>.Instance);
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        var recovery = Substitute.For<IPublicationRecoveryCoordinator>();
        var violations = new RolloutReadOnlyViolationMonitor();
        var directory = Path.Combine(Path.GetTempPath(), "startup-serving-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        options.DataDirectory = directory;
        try
        {
            var initializer = new StartupInitializer(persistence, source, festival, shop, lifetime,
                Options.Create(options), NullLogger<StartupInitializer>.Instance, state, violations,
                publicationRecovery: recovery);
            await initializer.StartAsync(CancellationToken.None);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await initializer.WaitForReadyAsync(deadline.Token);
            lifetime.DidNotReceive().StopApplication();
            Assert.Equal(expectReadOnly, initializer.ReadOnlyServing);
            Assert.Equal(!expectReadOnly, initializer.MutationReady);
            Assert.Equal(expectReadOnly, initializer.PostgresDefaultTransactionReadOnly);
            var health = await initializer.CheckHealthAsync(new HealthCheckContext());
            Assert.Equal(HealthStatus.Healthy, health.Status);
            var response = await ReadinessHealthTests.ProbeAsync(health);
            Assert.Equal(HttpStatusCode.OK, response.Status);
            Assert.Equal(expectReadOnly ? "degraded_read_only" : "ready",
                response.Json.GetProperty("startup").GetProperty("state").GetString());
            Assert.Equal(!expectReadOnly, response.Json.GetProperty("startup").GetProperty("mutationReady").GetBoolean());
            Assert.False(violations.HasViolation);
            if (expectReadOnly)
            {
                Assert.Contains("degraded_read_only", health.Description, StringComparison.Ordinal);
                Assert.Contains(state.Reason!, health.Description, StringComparison.Ordinal);
                recovery.DidNotReceive().RunOnce();
                Assert.Equal(0, http.Calls);
                using var connection = source.OpenConnection();
                using var read = connection.CreateCommand();
                read.CommandText = "SELECT published_scrape_id FROM public.scrape_publication_state";
                Assert.NotNull(read.ExecuteScalar());
                read.CommandText = "UPDATE public.scrape_publication_state SET updated_at=now()";
                Assert.Equal("25006", Assert.Throws<PostgresException>(() => read.ExecuteNonQuery()).SqlState);
                var coordinator = new PublicationRecoveryCoordinator(meta,
                    Options.Create(new PublicationCommitOptions()), Options.Create(options),
                    NullLogger<PublicationRecoveryCoordinator>.Instance, state);
                Assert.False(coordinator.RunOnce().BandSweep.Completed);
                var mutations = new RegistrationMutationCoordinator(meta, Substitute.For<IPathDataStore>(),
                    Substitute.For<ISongInstrumentSupportCache>(), state);
                await Assert.ThrowsAsync<RegistrationMutationBlockedException>(
                    () => mutations.TryAcquireWriteLeaseAsync());
            }
            await initializer.StopAsync(CancellationToken.None);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
        return festival;
    }

    private long Publish()
    {
        var scrape = _fixture.Db.StartScrapeRun();
        _fixture.Db.CompleteScrapeRun(scrape, 0, 0, 0, 0);
        _fixture.Db.PublishScrapeRun(scrape, promoteCachedResponses: false);
        return _fixture.Db.GetPublicationPointerState().CurrentPublicationId!.Value;
    }

    private string? Binding(long publication)
    {
        using var connection = _fixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT to_jsonb(binding)::text FROM publication_surface_bindings binding
            WHERE publication_id=@publication AND surface_name='path_artifacts'
            """;
        command.Parameters.AddWithValue("publication", publication);
        return (string?)command.ExecuteScalar();
    }

    private void Execute(string sql, Action<NpgsqlCommand> configure)
    {
        using var connection = _fixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure(command);
        command.ExecuteNonQuery();
    }

    private sealed class PoolSelectionFactory(
        string connectionString, Action<IServiceProvider> pipelineProbe) : WebApplicationFactory<Program>
    {
        private readonly string _dataDirectory = Path.Combine(
            Path.GetTempPath(), "publication-startup-host-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:PostgreSQL"] = connectionString,
                    ["Api:ApiKey"] = "isolated-startup-selection-fixture",
                }));
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<ScraperOptions>(options =>
                {
                    options.ApiOnly = true;
                    options.DataDirectory = _dataDirectory;
                });
                services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(_dataDirectory));
                services.RemoveAll<IHostedService>();
                services.AddSingleton<IStartupFilter>(new PipelineProbe(pipelineProbe));
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (Directory.Exists(_dataDirectory))
                Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    private sealed class PipelineProbe(Action<IServiceProvider> probe) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            probe(app.ApplicationServices);
            next(app);
        };
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{}") });
        }
    }

    private sealed class HostedCalls
    {
        public int Constructed;
        public int Started;
        public int Stopped;
    }

    private sealed class UnexpectedHostedWriter : IHostedService
    {
        private readonly HostedCalls _calls;
        public UnexpectedHostedWriter(HostedCalls calls)
        {
            _calls = calls;
            calls.Constructed++;
        }
        public Task StartAsync(CancellationToken ct) { _calls.Started++; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { _calls.Stopped++; return Task.CompletedTask; }
    }
}
