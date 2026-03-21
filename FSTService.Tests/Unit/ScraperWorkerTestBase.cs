using System.Reflection;
using FortniteFestival.Core;
using FortniteFestival.Core.Services;
using FSTService.Api;
using FSTService.Auth;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FSTService.Tests.Unit;

/// <summary>
/// Shared infrastructure for ScraperWorker test classes.
/// Each subclass gets its own instance per test (xUnit creates a new instance per [Fact]).
/// Splitting into multiple classes enables xUnit's default cross-class parallelism.
/// </summary>
public abstract class ScraperWorkerTestBase : IDisposable
{
    protected readonly string _tempDir;
    protected readonly MetaDatabase _metaDb;
    protected readonly GlobalLeaderboardPersistence _persistence;

    protected readonly TokenManager _tokenManager;
    protected readonly GlobalLeaderboardScraper _scraper;
    protected readonly AccountNameResolver _nameResolver;
    protected readonly PersonalDbBuilder _personalDbBuilder;
    protected readonly ScoreBackfiller _backfiller;
    protected readonly BackfillQueue _backfillQueue;
    protected readonly PostScrapeRefresher _refresher;
    protected readonly HistoryReconstructor _historyReconstructor;
    protected readonly FirstSeenSeasonCalculator _firstSeenCalculator;
    protected readonly FestivalService _festivalService;
    protected readonly ScrapeProgressTracker _progress;
    protected readonly IHostApplicationLifetime _lifetime;
    protected readonly ILogger<ScraperWorker> _log;

    protected ScraperWorkerTestBase()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sw_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _metaDb = new MetaDatabase(
            Path.Combine(_tempDir, "meta.db"),
            Substitute.For<ILogger<MetaDatabase>>());
        _metaDb.EnsureSchema();

        _persistence = new GlobalLeaderboardPersistence(
            _tempDir, _metaDb,
            Substitute.For<ILoggerFactory>(),
            Substitute.For<ILogger<GlobalLeaderboardPersistence>>());
        _persistence.Initialize();

        var noOpHandler = new NoOpHttpHandler();
        var dummyHttp = new HttpClient(noOpHandler);
        var epicAuth = new EpicAuthService(dummyHttp, Substitute.For<ILogger<EpicAuthService>>());

        _tokenManager = Substitute.For<TokenManager>(
            epicAuth,
            Substitute.For<ICredentialStore>(),
            Substitute.For<ILogger<TokenManager>>());

        _scraper = Substitute.For<GlobalLeaderboardScraper>(
            new HttpClient(),
            new ScrapeProgressTracker(),
            Substitute.For<ILogger<GlobalLeaderboardScraper>>(),
            0);

        _nameResolver = Substitute.For<AccountNameResolver>(
            new HttpClient(), _metaDb, _tokenManager,
            new ScrapeProgressTracker(),
            Substitute.For<ILogger<AccountNameResolver>>());

        _personalDbBuilder = Substitute.For<PersonalDbBuilder>(
            _persistence,
            new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null),
            _metaDb,
            _tempDir,
            Substitute.For<ILogger<PersonalDbBuilder>>());

        _backfiller = Substitute.For<ScoreBackfiller>(
            _scraper, _persistence,
            Substitute.For<ILogger<ScoreBackfiller>>());

        _backfillQueue = new BackfillQueue();

        _progress = new ScrapeProgressTracker();

        _refresher = Substitute.For<PostScrapeRefresher>(
            _scraper, _persistence,
            Substitute.For<ILogger<PostScrapeRefresher>>());

        _historyReconstructor = Substitute.For<HistoryReconstructor>(
            _scraper, _persistence, new HttpClient(), _progress,
            Substitute.For<ILogger<HistoryReconstructor>>());

        _firstSeenCalculator = Substitute.For<FirstSeenSeasonCalculator>(
            _scraper, _persistence, _progress,
            Substitute.For<ILogger<FirstSeenSeasonCalculator>>());

        _festivalService = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);

        _lifetime = Substitute.For<IHostApplicationLifetime>();
        _log = Substitute.For<ILogger<ScraperWorker>>();
    }

    public void Dispose()
    {
        _persistence.Dispose();
        _metaDb.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    protected ScraperWorker CreateWorker(ScraperOptions? opts = null)
        => CreateWorkerWithHttp(opts, null);

    protected ScraperWorker CreateWorkerWithHttp(ScraperOptions? opts, HttpMessageHandler? httpHandler)
    {
        var options = Options.Create(opts ?? new ScraperOptions
        {
            DataDirectory = _tempDir,
            DatabasePath = Path.Combine(_tempDir, "core.db"),
            DeviceAuthPath = Path.Combine(_tempDir, "device.json"),
        });

        var http = httpHandler != null ? new HttpClient(httpHandler) : new HttpClient();
        var pathGenerator = new PathGenerator(
            http,
            options,
            _progress,
            Substitute.For<ILogger<PathGenerator>>());

        var pathDataStore = new PathDataStore(
            Path.Combine(_tempDir, "core.db"));

        var notifications = new Api.NotificationService(Substitute.For<ILogger<Api.NotificationService>>());

        var rivalsCalculator = new RivalsCalculator(_persistence, Substitute.For<ILogger<RivalsCalculator>>());
        var rivalsOrchestrator = new RivalsOrchestrator(rivalsCalculator, _persistence, new Api.NotificationService(Substitute.For<ILogger<Api.NotificationService>>()), _progress, Substitute.For<ILogger<RivalsOrchestrator>>());

        var postScrapeOrchestrator = new PostScrapeOrchestrator(
            _persistence, _firstSeenCalculator, _nameResolver,
            _personalDbBuilder, _refresher, rivalsOrchestrator, notifications,
            _tokenManager, _progress, options,
            Substitute.For<ILogger<PostScrapeOrchestrator>>());

        var backfillOrchestrator = new BackfillOrchestrator(
            _backfiller, _backfillQueue, _historyReconstructor,
            _personalDbBuilder, rivalsOrchestrator, notifications, _persistence,
            _tokenManager, _progress, options,
            Substitute.For<ILogger<BackfillOrchestrator>>());

        var dbInitializer = new DatabaseInitializer(
            _persistence, _festivalService, _lifetime,
            Substitute.For<ILogger<DatabaseInitializer>>());
        dbInitializer.StartAsync(CancellationToken.None);
        dbInitializer.WaitForReadyAsync().GetAwaiter().GetResult();

        return new ScraperWorker(
            _tokenManager, _scraper, _persistence,
            _festivalService, dbInitializer,
            postScrapeOrchestrator, backfillOrchestrator,
            pathGenerator, pathDataStore,
            _progress, options, _lifetime, _log);
    }

    protected BackfillOrchestrator CreateBackfillOrchestrator(ScraperOptions? opts = null)
    {
        var options = Options.Create(opts ?? new ScraperOptions
        {
            DataDirectory = _tempDir,
            DatabasePath = Path.Combine(_tempDir, "core.db"),
            DeviceAuthPath = Path.Combine(_tempDir, "device.json"),
        });
        var notifications = new Api.NotificationService(Substitute.For<ILogger<Api.NotificationService>>());
        var rivalsCalculator = new RivalsCalculator(_persistence, Substitute.For<ILogger<RivalsCalculator>>());
        var rivalsOrchestrator = new RivalsOrchestrator(rivalsCalculator, _persistence, new Api.NotificationService(Substitute.For<ILogger<Api.NotificationService>>()), _progress, Substitute.For<ILogger<RivalsOrchestrator>>());
        return new BackfillOrchestrator(
            _backfiller, _backfillQueue, _historyReconstructor,
            _personalDbBuilder, rivalsOrchestrator, notifications, _persistence,
            _tokenManager, _progress, options,
            Substitute.For<ILogger<BackfillOrchestrator>>());
    }

    protected async Task InvokePrivateAsync(ScraperWorker worker, string methodName, params object[] args)
    {
        var method = typeof(ScraperWorker).GetMethod(methodName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var result = method!.Invoke(worker, args);
        if (result is Task task) await task;
    }

    protected async Task<T> InvokePrivateAsync<T>(ScraperWorker worker, string methodName, params object[] args)
    {
        var method = typeof(ScraperWorker).GetMethod(methodName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var result = method!.Invoke(worker, args);
        if (result is Task<T> typedTask) return await typedTask;
        throw new InvalidOperationException($"Method {methodName} did not return Task<{typeof(T).Name}>");
    }

    protected static FestivalService CreateServiceWithSongs(params (string id, string title, string artist)[] songs)
    {
        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var songsField = typeof(FestivalService).GetField("_songs", flags)!;
        var dirtyField = typeof(FestivalService).GetField("_songsDirty", flags)!;
        var dict = (Dictionary<string, Song>)songsField.GetValue(service)!;
        foreach (var (id, title, artist) in songs)
        {
            dict[id] = new Song
            {
                track = new Track
                {
                    su = id, tt = title, an = artist,
                    @in = new In { gr = 5, ba = 3, vl = 4, ds = 2 }
                }
            };
        }
        dirtyField.SetValue(service, true);
        return service;
    }

    protected static void WriteBE(Stream s, int v) { s.WriteByte((byte)(v>>24)); s.WriteByte((byte)(v>>16)); s.WriteByte((byte)(v>>8)); s.WriteByte((byte)v); }
    protected static void WriteBE16(Stream s, int v) { s.WriteByte((byte)(v>>8)); s.WriteByte((byte)v); }

    protected sealed class NoOpHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
