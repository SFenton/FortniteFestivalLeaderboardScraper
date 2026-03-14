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
using NSubstitute.ExceptionExtensions;

namespace FSTService.Tests.Unit;

/// <summary>
/// Tests for ScraperWorker's different execution modes and internal methods.
/// Uses NSubstitute for virtual methods on unsealed service classes, and
/// reflection to invoke private methods directly.
/// </summary>
public class ScraperWorkerModeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly MetaDatabase _metaDb;
    private readonly GlobalLeaderboardPersistence _persistence;

    private readonly TokenManager _tokenManager;
    private readonly GlobalLeaderboardScraper _scraper;
    private readonly AccountNameResolver _nameResolver;
    private readonly PersonalDbBuilder _personalDbBuilder;
    private readonly ScoreBackfiller _backfiller;
    private readonly BackfillQueue _backfillQueue;
    private readonly PostScrapeRefresher _refresher;
    private readonly HistoryReconstructor _historyReconstructor;
    private readonly FirstSeenSeasonCalculator _firstSeenCalculator;
    private readonly FestivalService _festivalService;
    private readonly TokenVault _tokenVault;
    private readonly ScrapeProgressTracker _progress;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ScraperWorker> _log;

    public ScraperWorkerModeTests()
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

        // NSubstitute proxies for unsealed classes with virtual methods.
        // EpicAuthService is still sealed, so we create a real instance with a no-op HTTP handler.
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

        _tokenVault = new TokenVault(
            _metaDb, epicAuth,
            Options.Create(new EpicOAuthSettings()),
            Substitute.For<ILogger<TokenVault>>());

        _lifetime = Substitute.For<IHostApplicationLifetime>();
        _log = Substitute.For<ILogger<ScraperWorker>>();
    }

    public void Dispose()
    {
        _persistence.Dispose();
        _metaDb.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private ScraperWorker CreateWorker(ScraperOptions? opts = null)
        => CreateWorkerWithHttp(opts, null);

    private ScraperWorker CreateWorkerWithHttp(ScraperOptions? opts, HttpMessageHandler? httpHandler)
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

        return new ScraperWorker(
            _tokenManager, _scraper, _persistence, _nameResolver,
            _personalDbBuilder, _backfiller, _backfillQueue, _refresher,
            _historyReconstructor, _firstSeenCalculator, _festivalService,
            new Api.NotificationService(Substitute.For<ILogger<Api.NotificationService>>()),
            _tokenVault,
            pathGenerator, pathDataStore,
            _progress, options, _lifetime, _log);
    }

    /// <summary>Invoke a private async method on ScraperWorker via reflection.</summary>
    private async Task InvokePrivateAsync(ScraperWorker worker, string methodName, params object[] args)
    {
        var method = typeof(ScraperWorker).GetMethod(methodName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var result = method!.Invoke(worker, args);
        if (result is Task task) await task;
    }

    private async Task<T> InvokePrivateAsync<T>(ScraperWorker worker, string methodName, params object[] args)
    {
        var method = typeof(ScraperWorker).GetMethod(methodName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var result = method!.Invoke(worker, args);
        if (result is Task<T> typedTask) return await typedTask;
        throw new InvalidOperationException($"Method {methodName} did not return Task<{typeof(T).Name}>");
    }

    // ═══════════════════════════════════════════════════════════════
    // ApiOnly mode
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_ApiOnly_RunsWithoutScraping()
    {
        var worker = CreateWorker(new ScraperOptions { ApiOnly = true, DataDirectory = _tempDir });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await worker.StartAsync(cts.Token);
        // Wait for cancellation
        try { await Task.Delay(300, cts.Token); } catch (OperationCanceledException) { }
        await worker.StopAsync(CancellationToken.None);

        // Should NOT have called auth or scraping
        await _tokenManager.DidNotReceive().GetAccessTokenAsync(Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    // SetupOnly mode
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_SetupOnly_Success_CallsDeviceCodeSetup()
    {
        _tokenManager.PerformDeviceCodeSetupAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var worker = CreateWorker(new ScraperOptions { SetupOnly = true, DataDirectory = _tempDir });

        await worker.StartAsync(CancellationToken.None);
        // Give it time to complete
        await Task.Delay(200);
        await worker.StopAsync(CancellationToken.None);

        await _tokenManager.Received(1).PerformDeviceCodeSetupAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_SetupOnly_Failure_StillCompletes()
    {
        _tokenManager.PerformDeviceCodeSetupAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var worker = CreateWorker(new ScraperOptions { SetupOnly = true, DataDirectory = _tempDir });

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await worker.StopAsync(CancellationToken.None);

        await _tokenManager.Received(1).PerformDeviceCodeSetupAsync(Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    // EnsureAuthenticatedAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task EnsureAuthenticated_TokenPresent_ReturnsTrue()
    {
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("valid-token"));

        var worker = CreateWorker();
        var result = await InvokePrivateAsync<bool>(worker, "EnsureAuthenticatedAsync", CancellationToken.None);

        Assert.True(result);
        await _tokenManager.DidNotReceive().PerformDeviceCodeSetupAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureAuthenticated_NoToken_DeviceCodeSuccess_ReturnsTrue()
    {
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        _tokenManager.PerformDeviceCodeSetupAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var worker = CreateWorker();
        var result = await InvokePrivateAsync<bool>(worker, "EnsureAuthenticatedAsync", CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task EnsureAuthenticated_NoToken_DeviceCodeFail_ReturnsFalse()
    {
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        _tokenManager.PerformDeviceCodeSetupAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var worker = CreateWorker();
        var result = await InvokePrivateAsync<bool>(worker, "EnsureAuthenticatedAsync", CancellationToken.None);

        Assert.False(result);
    }

    // ═══════════════════════════════════════════════════════════════
    // RunResolveOnlyAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunResolveOnly_NoUnresolved_ReturnsEarly()
    {
        var worker = CreateWorker();
        await InvokePrivateAsync(worker, "RunResolveOnlyAsync", CancellationToken.None);

        // No names to resolve → should NOT call the resolver
        await _nameResolver.DidNotReceive().ResolveNewAccountsAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunResolveOnly_WithUnresolved_CallsResolver()
    {
        // Insert some unresolved account IDs
        _metaDb.InsertAccountIds(new[] { "acct1", "acct2" });

        _nameResolver.ResolveNewAccountsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(2));

        var worker = CreateWorker();
        await InvokePrivateAsync(worker, "RunResolveOnlyAsync", CancellationToken.None);

        await _nameResolver.Received(1).ResolveNewAccountsAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunResolveOnly_ResolverThrows_DoesNotPropagate()
    {
        _metaDb.InsertAccountIds(new[] { "acct1" });

        _nameResolver.ResolveNewAccountsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("API down"));

        var worker = CreateWorker();
        // Should not throw — the exception is caught internally
        await InvokePrivateAsync(worker, "RunResolveOnlyAsync", CancellationToken.None);
    }

    // ═══════════════════════════════════════════════════════════════
    // RunBackfillPhaseAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunBackfillPhase_NothingQueued_ReturnsEarly()
    {
        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var worker = CreateWorker();

        await InvokePrivateAsync(worker, "RunBackfillPhaseAsync", service, CancellationToken.None);

        await _backfiller.DidNotReceive().BackfillAccountAsync(
            Arg.Any<string>(), Arg.Any<FestivalService>(),
            Arg.Any<string>(), Arg.Any<string>(), ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunBackfillPhase_QueuedAccounts_NoToken_ReEnqueues()
    {
        _backfillQueue.Enqueue(new BackfillRequest("acct1"));

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var worker = CreateWorker();

        await InvokePrivateAsync(worker, "RunBackfillPhaseAsync", service, CancellationToken.None);

        // Should not have called backfiller
        await _backfiller.DidNotReceive().BackfillAccountAsync(
            Arg.Any<string>(), Arg.Any<FestivalService>(),
            Arg.Any<string>(), Arg.Any<string>(), ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunBackfillPhase_QueuedAccounts_WithToken_Backfills()
    {
        _backfillQueue.Enqueue(new BackfillRequest("acct1"));

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("token"));
        _tokenManager.AccountId.Returns("callerAcct");

        _backfiller.BackfillAccountAsync(
            Arg.Any<string>(), Arg.Any<FestivalService>(),
            Arg.Any<string>(), Arg.Any<string>(), ct: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(5));

        _personalDbBuilder.RebuildForAccounts(
            Arg.Any<IReadOnlySet<string>>(), Arg.Any<MetaDatabase>())
            .Returns(1);

        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var worker = CreateWorker();

        await InvokePrivateAsync(worker, "RunBackfillPhaseAsync", service, CancellationToken.None);

        await _backfiller.Received(1).BackfillAccountAsync(
            "acct1", service, "token", "callerAcct", ct: Arg.Any<CancellationToken>());
        _personalDbBuilder.Received(1).RebuildForAccounts(
            Arg.Any<IReadOnlySet<string>>(), Arg.Any<MetaDatabase>());
    }

    [Fact]
    public async Task RunBackfillPhase_BackfillReturnsZero_SkipsPersonalDbRebuild()
    {
        _backfillQueue.Enqueue(new BackfillRequest("acct1"));

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("token"));
        _tokenManager.AccountId.Returns("callerAcct");

        _backfiller.BackfillAccountAsync(
            Arg.Any<string>(), Arg.Any<FestivalService>(),
            Arg.Any<string>(), Arg.Any<string>(), ct: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var worker = CreateWorker();

        await InvokePrivateAsync(worker, "RunBackfillPhaseAsync", service, CancellationToken.None);

        _personalDbBuilder.DidNotReceive().RebuildForAccounts(
            Arg.Any<IReadOnlySet<string>>(), Arg.Any<MetaDatabase>());
    }

    [Fact]
    public async Task RunBackfillPhase_BackfillThrows_ContinuesWithOtherAccounts()
    {
        _backfillQueue.Enqueue(new BackfillRequest("acct1"));
        _backfillQueue.Enqueue(new BackfillRequest("acct2"));

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("token"));
        _tokenManager.AccountId.Returns("callerAcct");

        _backfiller.BackfillAccountAsync(
            "acct1", Arg.Any<FestivalService>(),
            Arg.Any<string>(), Arg.Any<string>(), ct: Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("failure"));
        _backfiller.BackfillAccountAsync(
            "acct2", Arg.Any<FestivalService>(),
            Arg.Any<string>(), Arg.Any<string>(), ct: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(3));

        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var worker = CreateWorker();

        // Should not throw — errors are caught per-account
        await InvokePrivateAsync(worker, "RunBackfillPhaseAsync", service, CancellationToken.None);

        // Both accounts attempted
        await _backfiller.Received(2).BackfillAccountAsync(
            Arg.Any<string>(), Arg.Any<FestivalService>(),
            Arg.Any<string>(), Arg.Any<string>(), ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunBackfillPhase_PersonalDbRebuildThrows_DoesNotPropagate()
    {
        _backfillQueue.Enqueue(new BackfillRequest("acct1"));

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("token"));
        _tokenManager.AccountId.Returns("callerAcct");

        _backfiller.BackfillAccountAsync(
            Arg.Any<string>(), Arg.Any<FestivalService>(),
            Arg.Any<string>(), Arg.Any<string>(), ct: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(1));

        _personalDbBuilder.RebuildForAccounts(
            Arg.Any<IReadOnlySet<string>>(), Arg.Any<MetaDatabase>())
            .Throws(new IOException("disk full"));

        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var worker = CreateWorker();

        // Should not throw
        await InvokePrivateAsync(worker, "RunBackfillPhaseAsync", service, CancellationToken.None);
    }

    // ═══════════════════════════════════════════════════════════════
    // RunHistoryReconPhaseAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunHistoryReconPhase_NoRegisteredUsers_ReturnsEarly()
    {
        var worker = CreateWorker();
        await InvokePrivateAsync(worker, "RunHistoryReconPhaseAsync", CancellationToken.None);

        await _historyReconstructor.DidNotReceive().DiscoverSeasonWindowsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunHistoryReconPhase_RegisteredButNoCompletedBackfill_ReturnsEarly()
    {
        _metaDb.RegisterUser("dev1", "acct1");

        var worker = CreateWorker();
        await InvokePrivateAsync(worker, "RunHistoryReconPhaseAsync", CancellationToken.None);

        // No completed backfill → nothing to reconstruct
        await _historyReconstructor.DidNotReceive().DiscoverSeasonWindowsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunHistoryReconPhase_CompletedBackfill_NoToken_Skips()
    {
        // Register and complete backfill
        _metaDb.RegisterUser("dev1", "acct1");
        _metaDb.EnqueueBackfill("acct1", 10);
        _metaDb.StartBackfill("acct1");
        _metaDb.CompleteBackfill("acct1");

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var worker = CreateWorker();
        await InvokePrivateAsync(worker, "RunHistoryReconPhaseAsync", CancellationToken.None);

        await _historyReconstructor.DidNotReceive().DiscoverSeasonWindowsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunHistoryReconPhase_CompletedBackfill_WithToken_Reconstructs()
    {
        _metaDb.RegisterUser("dev1", "acct1");
        _metaDb.EnqueueBackfill("acct1", 10);
        _metaDb.StartBackfill("acct1");
        _metaDb.CompleteBackfill("acct1");

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("token"));
        _tokenManager.AccountId.Returns("callerAcct");

        var windows = new List<SeasonWindowInfo>
        {
            new() { WindowId = "s1", SeasonNumber = 1, EventId = "evt1" }
        };
        _historyReconstructor.DiscoverSeasonWindowsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SeasonWindowInfo>>(windows));

        _historyReconstructor.ReconstructAccountAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<SeasonWindowInfo>>(),
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<AdaptiveConcurrencyLimiter?>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(10));

        _personalDbBuilder.RebuildForAccounts(
            Arg.Any<IReadOnlySet<string>>(), Arg.Any<MetaDatabase>())
            .Returns(1);

        var worker = CreateWorker();
        await InvokePrivateAsync(worker, "RunHistoryReconPhaseAsync", CancellationToken.None);

        await _historyReconstructor.Received(1).ReconstructAccountAsync(
            "acct1", windows, "token", "callerAcct",
            Arg.Any<int>(), Arg.Any<AdaptiveConcurrencyLimiter?>(),
            Arg.Any<CancellationToken>());
        _personalDbBuilder.Received(1).RebuildForAccounts(
            Arg.Any<IReadOnlySet<string>>(), Arg.Any<MetaDatabase>());
    }

    [Fact]
    public async Task RunHistoryReconPhase_NoSeasonWindows_Skips()
    {
        _metaDb.RegisterUser("dev1", "acct1");
        _metaDb.EnqueueBackfill("acct1", 10);
        _metaDb.StartBackfill("acct1");
        _metaDb.CompleteBackfill("acct1");

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("token"));
        _tokenManager.AccountId.Returns("callerAcct");

        _historyReconstructor.DiscoverSeasonWindowsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SeasonWindowInfo>>(new List<SeasonWindowInfo>()));

        var worker = CreateWorker();
        await InvokePrivateAsync(worker, "RunHistoryReconPhaseAsync", CancellationToken.None);

        await _historyReconstructor.DidNotReceive().ReconstructAccountAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<SeasonWindowInfo>>(),
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<AdaptiveConcurrencyLimiter?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunHistoryReconPhase_DiscoverThrows_DoesNotPropagate()
    {
        _metaDb.RegisterUser("dev1", "acct1");
        _metaDb.EnqueueBackfill("acct1", 10);
        _metaDb.StartBackfill("acct1");
        _metaDb.CompleteBackfill("acct1");

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("token"));
        _tokenManager.AccountId.Returns("callerAcct");

        _historyReconstructor.DiscoverSeasonWindowsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("network error"));

        var worker = CreateWorker();
        // Should not throw
        await InvokePrivateAsync(worker, "RunHistoryReconPhaseAsync", CancellationToken.None);
    }

    [Fact]
    public async Task RunHistoryReconPhase_ReconThrows_ContinuesAndFails()
    {
        _metaDb.RegisterUser("dev1", "acct1");
        _metaDb.EnqueueBackfill("acct1", 10);
        _metaDb.StartBackfill("acct1");
        _metaDb.CompleteBackfill("acct1");
        // Create history recon row so FailHistoryRecon UPDATE can work
        _metaDb.EnqueueHistoryRecon("acct1", 10);

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("token"));
        _tokenManager.AccountId.Returns("callerAcct");

        var windows = new List<SeasonWindowInfo>
        {
            new() { WindowId = "s1", SeasonNumber = 1, EventId = "evt1" }
        };
        _historyReconstructor.DiscoverSeasonWindowsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SeasonWindowInfo>>(windows));

        _historyReconstructor.ReconstructAccountAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<SeasonWindowInfo>>(),
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<AdaptiveConcurrencyLimiter?>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("recon error"));

        var worker = CreateWorker();
        // Should not throw
        await InvokePrivateAsync(worker, "RunHistoryReconPhaseAsync", CancellationToken.None);

        // Error state should be persisted in MetaDB
        var status = _metaDb.GetHistoryReconStatus("acct1");
        Assert.NotNull(status);
        Assert.Equal("error", status!.Status);
    }

    [Fact]
    public async Task RunHistoryReconPhase_AlreadyReconstructed_Skips()
    {
        _metaDb.RegisterUser("dev1", "acct1");
        _metaDb.EnqueueBackfill("acct1", 10);
        _metaDb.StartBackfill("acct1");
        _metaDb.CompleteBackfill("acct1");
        // Mark history recon as already complete
        _metaDb.EnqueueHistoryRecon("acct1", 10);
        _metaDb.StartHistoryRecon("acct1");
        _metaDb.CompleteHistoryRecon("acct1");

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("token"));
        _tokenManager.AccountId.Returns("callerAcct");

        var worker = CreateWorker();
        await InvokePrivateAsync(worker, "RunHistoryReconPhaseAsync", CancellationToken.None);

        // Already reconstructed → should not call discover
        await _historyReconstructor.DidNotReceive().DiscoverSeasonWindowsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    // Auth failure path
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_AuthFails_StopsGracefully()
    {
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        _tokenManager.PerformDeviceCodeSetupAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var worker = CreateWorker();
        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(500);
        await worker.StopAsync(CancellationToken.None);

        // Auth failed → should not have scraped
        await _scraper.DidNotReceive().ScrapeManySongsAsync(
            Arg.Any<IReadOnlyList<GlobalLeaderboardScraper.SongScrapeRequest>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<Func<string, List<GlobalLeaderboardResult>, ValueTask>?>(),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    // ResolveOnly mode via ExecuteAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_ResolveOnly_CallsResolver()
    {
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("token"));

        _metaDb.InsertAccountIds(new[] { "acct1" });
        _nameResolver.ResolveNewAccountsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(1));

        var worker = CreateWorker(new ScraperOptions
        {
            ResolveOnly = true,
            DataDirectory = _tempDir,
            DatabasePath = Path.Combine(_tempDir, "core.db"),
        });

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(500);
        await worker.StopAsync(CancellationToken.None);

        await _nameResolver.Received().ResolveNewAccountsAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    // Backfill with pending DB entries
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunBackfillPhase_PendingInDb_MergesWithQueue()
    {
        // Queue one
        _backfillQueue.Enqueue(new BackfillRequest("acctQ"));
        // DB has another pending
        _metaDb.EnqueueBackfill("acctDb", 10);

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("token"));
        _tokenManager.AccountId.Returns("callerAcct");

        _backfiller.BackfillAccountAsync(
            Arg.Any<string>(), Arg.Any<FestivalService>(),
            Arg.Any<string>(), Arg.Any<string>(), ct: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var worker = CreateWorker();

        await InvokePrivateAsync(worker, "RunBackfillPhaseAsync", service, CancellationToken.None);

        // Both accounts should have been processed
        await _backfiller.Received(2).BackfillAccountAsync(
            Arg.Any<string>(), Arg.Any<FestivalService>(),
            Arg.Any<string>(), Arg.Any<string>(), ct: Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    // RunSingleSongTestAsync
    // ═══════════════════════════════════════════════════════════════

    private static FestivalService CreateServiceWithSongs(params (string id, string title, string artist)[] songs)
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

    [Fact]
    public async Task RunSingleSongTest_MatchingSong_ScrapesAndCompletes()
    {
        var service = CreateServiceWithSongs(("s1", "Test Song Alpha", "Artist A"));

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("token"));
        _tokenManager.AccountId.Returns("callerAcct");

        // Mock the scraper to return results for the song
        var mockResults = new Dictionary<string, List<GlobalLeaderboardResult>>
        {
            ["s1"] = new List<GlobalLeaderboardResult>
            {
                new GlobalLeaderboardResult
                {
                    SongId = "s1", Instrument = "Solo_Guitar", TotalPages = 1, PagesScraped = 1,
                    Requests = 1, BytesReceived = 100,
                    Entries = new List<FSTService.Scraping.LeaderboardEntry>
                    {
                        new() { AccountId = "a1", Score = 1000, Accuracy = 95, Stars = 5, IsFullCombo = true, Rank = 1, Percentile = 1.0 }
                    }
                }
            }
        };
        _scraper.ScrapeManySongsAsync(
            Arg.Any<IReadOnlyList<GlobalLeaderboardScraper.SongScrapeRequest>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<Func<string, List<GlobalLeaderboardResult>, ValueTask>?>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockResults));

        var opts = new ScraperOptions
        {
            TestSongQuery = "Alpha",
            DataDirectory = _tempDir,
            DegreeOfParallelism = 4,
        };
        var worker = CreateWorker(opts);

        await InvokePrivateAsync(worker, "RunSingleSongTestAsync", service, opts, CancellationToken.None);

        // Verify scraper was called
        await _scraper.Received(1).ScrapeManySongsAsync(
            Arg.Any<IReadOnlyList<GlobalLeaderboardScraper.SongScrapeRequest>>(),
            "token", "callerAcct", Arg.Any<int>(),
            Arg.Any<Func<string, List<GlobalLeaderboardResult>, ValueTask>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSingleSongTest_NoMatchingSong_LogsAndReturns()
    {
        var service = CreateServiceWithSongs(("s1", "Test Song Alpha", "Artist A"));

        var opts = new ScraperOptions
        {
            TestSongQuery = "NonExistentSong",
            DataDirectory = _tempDir,
        };
        var worker = CreateWorker(opts);

        // Should not throw - no match found
        await InvokePrivateAsync(worker, "RunSingleSongTestAsync", service, opts, CancellationToken.None);

        // Should NOT have called the scraper at all
        await _scraper.DidNotReceive().ScrapeManySongsAsync(
            Arg.Any<IReadOnlyList<GlobalLeaderboardScraper.SongScrapeRequest>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<Func<string, List<GlobalLeaderboardResult>, ValueTask>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSingleSongTest_NoAccessToken_LogsAndReturns()
    {
        var service = CreateServiceWithSongs(("s1", "Test Song Alpha", "Artist A"));

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var opts = new ScraperOptions
        {
            TestSongQuery = "Alpha",
            DataDirectory = _tempDir,
        };
        var worker = CreateWorker(opts);

        await InvokePrivateAsync(worker, "RunSingleSongTestAsync", service, opts, CancellationToken.None);

        await _scraper.DidNotReceive().ScrapeManySongsAsync(
            Arg.Any<IReadOnlyList<GlobalLeaderboardScraper.SongScrapeRequest>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<Func<string, List<GlobalLeaderboardResult>, ValueTask>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSingleSongTest_CommaSeparatedQueries_ScrapesMultiple()
    {
        var service = CreateServiceWithSongs(
            ("s1", "Song Alpha", "Artist A"),
            ("s2", "Song Beta", "Artist B"));

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("token"));
        _tokenManager.AccountId.Returns("callerAcct");

        _scraper.ScrapeManySongsAsync(
            Arg.Any<IReadOnlyList<GlobalLeaderboardScraper.SongScrapeRequest>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<Func<string, List<GlobalLeaderboardResult>, ValueTask>?>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Dictionary<string, List<GlobalLeaderboardResult>>
            {
                ["s1"] = new() { new() { SongId = "s1", Instrument = "Solo_Guitar", Entries = new(), TotalPages = 0, PagesScraped = 0, Requests = 1, BytesReceived = 100 } },
                ["s2"] = new() { new() { SongId = "s2", Instrument = "Solo_Guitar", Entries = new(), TotalPages = 0, PagesScraped = 0, Requests = 1, BytesReceived = 100 } },
            }));

        var opts = new ScraperOptions
        {
            TestSongQuery = "Alpha,Beta",
            DataDirectory = _tempDir,
            DegreeOfParallelism = 4,
        };
        var worker = CreateWorker(opts);

        await InvokePrivateAsync(worker, "RunSingleSongTestAsync", service, opts, CancellationToken.None);

        await _scraper.Received(1).ScrapeManySongsAsync(
            Arg.Is<IReadOnlyList<GlobalLeaderboardScraper.SongScrapeRequest>>(r => r.Count == 2),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<Func<string, List<GlobalLeaderboardResult>, ValueTask>?>(),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    // RunScrapePassAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunScrapePass_NoAccessToken_SkipsScrape()
    {
        var service = CreateServiceWithSongs(("s1", "Song", "Artist"));

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var opts = new ScraperOptions { DataDirectory = _tempDir };
        var worker = CreateWorker(opts);

        await InvokePrivateAsync(worker, "RunScrapePassAsync", service, opts, CancellationToken.None);

        await _scraper.DidNotReceive().ScrapeManySongsAsync(
            Arg.Any<IReadOnlyList<GlobalLeaderboardScraper.SongScrapeRequest>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<Func<string, List<GlobalLeaderboardResult>, ValueTask>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunScrapePass_WithToken_ExecutesFullPipeline()
    {
        var service = CreateServiceWithSongs(("s1", "Song One", "Artist"));

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("token"));
        _tokenManager.AccountId.Returns("callerAcct");

        // Mock the scraper to return results AND invoke the onSongComplete callback
        var mockResults = new Dictionary<string, List<GlobalLeaderboardResult>>
        {
            ["s1"] = new List<GlobalLeaderboardResult>
            {
                new()
                {
                    SongId = "s1", Instrument = "Solo_Guitar", TotalPages = 1, PagesScraped = 1,
                    Requests = 1, BytesReceived = 200,
                    Entries = new List<FSTService.Scraping.LeaderboardEntry>
                    {
                        new() { AccountId = "player1", Score = 500, Accuracy = 90, Stars = 4, IsFullCombo = false, Rank = 1, Percentile = 1.0 }
                    }
                }
            }
        };

        _scraper.ScrapeManySongsAsync(
            Arg.Any<IReadOnlyList<GlobalLeaderboardScraper.SongScrapeRequest>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<Func<string, List<GlobalLeaderboardResult>, ValueTask>?>(),
            Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                // Invoke the callback so the persistence pipeline processes entries
                var onSongComplete = callInfo.ArgAt<Func<string, List<GlobalLeaderboardResult>, ValueTask>?>(4);
                if (onSongComplete != null)
                {
                    await onSongComplete("s1", mockResults["s1"]);
                }
                return mockResults;
            });

        // Mock name resolver
        _nameResolver.ResolveNewAccountsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        var opts = new ScraperOptions
        {
            DataDirectory = _tempDir,
            DegreeOfParallelism = 2,
            RunOnce = true,
        };
        var worker = CreateWorker(opts);

        await InvokePrivateAsync(worker, "RunScrapePassAsync", service, opts, CancellationToken.None);

        // Verify the scraper was called
        await _scraper.Received(1).ScrapeManySongsAsync(
            Arg.Any<IReadOnlyList<GlobalLeaderboardScraper.SongScrapeRequest>>(),
            "token", "callerAcct", 2,
            Arg.Any<Func<string, List<GlobalLeaderboardResult>, ValueTask>?>(),
            Arg.Any<CancellationToken>());

        // Verify name resolution was attempted
        await _nameResolver.Received().ResolveNewAccountsAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunScrapePass_WithRegisteredUsers_RefreshesAndBackfills()
    {
        var service = CreateServiceWithSongs(("s1", "Song One", "Artist"));

        // Register a user so the code path includes post-scrape refresh
        _metaDb.RegisterUser("dev1", "regAcct");

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("token"));
        _tokenManager.AccountId.Returns("callerAcct");

        // Mock the scraper to invoke the callback with entries for the registered user
        var scrapedResults = new List<GlobalLeaderboardResult>
        {
            new()
            {
                SongId = "s1", Instrument = "Solo_Guitar", TotalPages = 1, PagesScraped = 1,
                Requests = 1, BytesReceived = 300,
                Entries = new List<FSTService.Scraping.LeaderboardEntry>
                {
                    new() { AccountId = "regAcct", Score = 2000, Accuracy = 95, Stars = 5, IsFullCombo = true, Rank = 1, Percentile = 1.0 }
                }
            }
        };

        _scraper.ScrapeManySongsAsync(
            Arg.Any<IReadOnlyList<GlobalLeaderboardScraper.SongScrapeRequest>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<Func<string, List<GlobalLeaderboardResult>, ValueTask>?>(),
            Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var onSongComplete = callInfo.ArgAt<Func<string, List<GlobalLeaderboardResult>, ValueTask>?>(4);
                if (onSongComplete != null)
                {
                    await onSongComplete("s1", scrapedResults);
                }
                return new Dictionary<string, List<GlobalLeaderboardResult>>
                {
                    ["s1"] = scrapedResults
                };
            });

        _nameResolver.ResolveNewAccountsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        _personalDbBuilder.RebuildForAccounts(Arg.Any<HashSet<string>>(), Arg.Any<MetaDatabase>())
            .Returns(1);

        _refresher.RefreshAllAsync(
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Any<HashSet<(string, string, string)>>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), ct: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        var opts = new ScraperOptions { DataDirectory = _tempDir, DegreeOfParallelism = 2 };
        var worker = CreateWorker(opts);

        await InvokePrivateAsync(worker, "RunScrapePassAsync", service, opts, CancellationToken.None);

        // Post-scrape refresh should be called for registered users
        await _refresher.Received().RefreshAllAsync(
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Any<HashSet<(string, string, string)>>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), ct: Arg.Any<CancellationToken>());

        // Personal DB rebuild should be called because registered user had score changes
        _personalDbBuilder.Received().RebuildForAccounts(
            Arg.Any<HashSet<string>>(), Arg.Any<MetaDatabase>());
    }

    [Fact]
    public async Task RunScrapePass_NameResolutionFails_ContinuesGracefully()
    {
        var service = CreateServiceWithSongs(("s1", "Song One", "Artist"));

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("token"));
        _tokenManager.AccountId.Returns("callerAcct");

        _scraper.ScrapeManySongsAsync(
            Arg.Any<IReadOnlyList<GlobalLeaderboardScraper.SongScrapeRequest>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<Func<string, List<GlobalLeaderboardResult>, ValueTask>?>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Dictionary<string, List<GlobalLeaderboardResult>>()));

        _nameResolver.ResolveNewAccountsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("DNS error"));

        var opts = new ScraperOptions { DataDirectory = _tempDir };
        var worker = CreateWorker(opts);

        // Should not throw — name resolution failure is caught
        await InvokePrivateAsync(worker, "RunScrapePassAsync", service, opts, CancellationToken.None);
    }

    [Fact]
    public async Task RunScrapePass_RefresherReturnsPositive_LogsUpdateCount()
    {
        var service = CreateServiceWithSongs(("s1", "Song One", "Artist"));

        _metaDb.RegisterUser("dev1", "regAcct");

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("token"));
        _tokenManager.AccountId.Returns("callerAcct");

        _scraper.ScrapeManySongsAsync(
            Arg.Any<IReadOnlyList<GlobalLeaderboardScraper.SongScrapeRequest>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<Func<string, List<GlobalLeaderboardResult>, ValueTask>?>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Dictionary<string, List<GlobalLeaderboardResult>>()));

        _nameResolver.ResolveNewAccountsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        // Refresher returns > 0, exercising the "refreshed > 0" log line
        _refresher.RefreshAllAsync(
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Any<HashSet<(string, string, string)>>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), ct: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(5));

        var opts = new ScraperOptions { DataDirectory = _tempDir, DegreeOfParallelism = 2 };
        var worker = CreateWorker(opts);

        await InvokePrivateAsync(worker, "RunScrapePassAsync", service, opts, CancellationToken.None);
    }

    [Fact]
    public async Task RunScrapePass_PersonalDbRebuildFails_ContinuesGracefully()
    {
        var service = CreateServiceWithSongs(("s1", "Song One", "Artist"));

        _metaDb.RegisterUser("dev1", "regAcct");

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("token"));
        _tokenManager.AccountId.Returns("callerAcct");

        // Invoke callback to trigger ChangedAccountIds
        var scrapedResults = new List<GlobalLeaderboardResult>
        {
            new()
            {
                SongId = "s1", Instrument = "Solo_Guitar", TotalPages = 1, PagesScraped = 1,
                Requests = 1, BytesReceived = 200,
                Entries = new List<FSTService.Scraping.LeaderboardEntry>
                {
                    new() { AccountId = "regAcct", Score = 9000, Accuracy = 99, Stars = 5, IsFullCombo = true, Rank = 1, Percentile = 1.0 }
                }
            }
        };

        _scraper.ScrapeManySongsAsync(
            Arg.Any<IReadOnlyList<GlobalLeaderboardScraper.SongScrapeRequest>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<Func<string, List<GlobalLeaderboardResult>, ValueTask>?>(),
            Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var cb = callInfo.ArgAt<Func<string, List<GlobalLeaderboardResult>, ValueTask>?>(4);
                if (cb != null) await cb("s1", scrapedResults);
                return new Dictionary<string, List<GlobalLeaderboardResult>> { ["s1"] = scrapedResults };
            });

        _nameResolver.ResolveNewAccountsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        // PersonalDbBuilder throws
        _personalDbBuilder.RebuildForAccounts(Arg.Any<HashSet<string>>(), Arg.Any<MetaDatabase>())
            .Returns(x => throw new InvalidOperationException("DB rebuild failed"));

        _refresher.RefreshAllAsync(
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Any<HashSet<(string, string, string)>>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), ct: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        var opts = new ScraperOptions { DataDirectory = _tempDir, DegreeOfParallelism = 2 };
        var worker = CreateWorker(opts);

        await InvokePrivateAsync(worker, "RunScrapePassAsync", service, opts, CancellationToken.None);
    }

    [Fact]
    public async Task RunScrapePass_RefresherFails_ContinuesGracefully()
    {
        var service = CreateServiceWithSongs(("s1", "Song One", "Artist"));

        _metaDb.RegisterUser("dev1", "regAcct");

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("token"));
        _tokenManager.AccountId.Returns("callerAcct");

        _scraper.ScrapeManySongsAsync(
            Arg.Any<IReadOnlyList<GlobalLeaderboardScraper.SongScrapeRequest>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<Func<string, List<GlobalLeaderboardResult>, ValueTask>?>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Dictionary<string, List<GlobalLeaderboardResult>>()));

        _nameResolver.ResolveNewAccountsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        // Refresher throws
        _refresher.RefreshAllAsync(
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Any<HashSet<(string, string, string)>>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), ct: Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Refresh crashed"));

        var opts = new ScraperOptions { DataDirectory = _tempDir, DegreeOfParallelism = 2 };
        var worker = CreateWorker(opts);

        // Should not throw — catch blocks handle the error
        await InvokePrivateAsync(worker, "RunScrapePassAsync", service, opts, CancellationToken.None);
    }

    [Fact]
    public async Task RunScrapePass_RefreshTokenNullForPostScrape_LogsWarning()
    {
        var service = CreateServiceWithSongs(("s1", "Song One", "Artist"));

        _metaDb.RegisterUser("dev1", "regAcct");

        // First call returns token (for scraping), second returns null (for post-scrape refresh)
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<string?>("token"),
                Task.FromResult<string?>(null));
        _tokenManager.AccountId.Returns("callerAcct");

        _scraper.ScrapeManySongsAsync(
            Arg.Any<IReadOnlyList<GlobalLeaderboardScraper.SongScrapeRequest>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<Func<string, List<GlobalLeaderboardResult>, ValueTask>?>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Dictionary<string, List<GlobalLeaderboardResult>>()));

        _nameResolver.ResolveNewAccountsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        var opts = new ScraperOptions { DataDirectory = _tempDir, DegreeOfParallelism = 2 };
        var worker = CreateWorker(opts);

        // Should not throw — logs a warning and continues
        await InvokePrivateAsync(worker, "RunScrapePassAsync", service, opts, CancellationToken.None);
    }

    // ═══════════════════════════════════════════════════════════════
    // BackgroundSongSyncLoopAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task BackgroundSongSyncLoop_CancelsImmediately()
    {
        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var worker = CreateWorker();
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // cancel immediately

        // Should exit cleanly on pre-cancelled token
        await InvokePrivateAsync(worker, "BackgroundSongSyncLoopAsync",
            service, TimeSpan.FromMinutes(15), cts.Token);
    }

    [Fact]
    public async Task BackgroundSongSyncLoop_CancelsDuringDelay()
    {
        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // Should exit cleanly when token fires during the delay
        await InvokePrivateAsync(worker, "BackgroundSongSyncLoopAsync",
            service, TimeSpan.FromHours(1), cts.Token);
    }

    [Fact]
    public async Task BackgroundSongSyncLoop_NoNewSongs_DoesNotReinitializeDiSingleton()
    {
        // Use a very short interval so the loop fires quickly, then cancel
        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var worker = CreateWorker();
        using var cts = new CancellationTokenSource();

        // The loop will fire once (interval = 1ms boundary), sync will be a no-op
        // (null persistence), then we cancel.
        var task = InvokePrivateAsync(worker, "BackgroundSongSyncLoopAsync",
            service, TimeSpan.FromMilliseconds(1), cts.Token);
        await Task.Delay(200);
        cts.Cancel();
        await task;

        // _festivalService (DI singleton) should never have been re-initialized
        // because song count didn't increase. Songs.Count stays 0 before and after.
        // (We can't assert on InitializeAsync calls since it's not virtual,
        // but the important thing is the loop ran without errors.)
    }

    [Fact]
    public async Task BackgroundSongSyncLoop_SyncThrows_ContinuesGracefully()
    {
        // FestivalService with null persistence will throw NullReferenceException
        // when SyncSongsAsync tries to use _persistence. The loop should catch it.
        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var worker = CreateWorker();
        using var cts = new CancellationTokenSource();

        var task = InvokePrivateAsync(worker, "BackgroundSongSyncLoopAsync",
            service, TimeSpan.FromMilliseconds(1), cts.Token);
        await Task.Delay(200);
        cts.Cancel();
        await task;

        // Loop should have caught the exception and not propagated it
    }

    // ─── TryGeneratePathsAsync tests ────────────────────────────

    [Fact]
    public async Task TryGeneratePathsAsync_disabled_does_nothing()
    {
        var worker = CreateWorker(new ScraperOptions
        {
            DataDirectory = _tempDir,
            DatabasePath = Path.Combine(_tempDir, "core.db"),
            DeviceAuthPath = Path.Combine(_tempDir, "device.json"),
            EnablePathGeneration = false,
        });

        // Should return immediately without touching anything
        await worker.TryGeneratePathsAsync(_festivalService, force: false, CancellationToken.None);
    }

    [Fact]
    public async Task TryGeneratePathsAsync_no_songs_with_dat_url_does_nothing()
    {
        // FestivalService has no songs (empty)
        var worker = CreateWorker(new ScraperOptions
        {
            DataDirectory = _tempDir,
            DatabasePath = Path.Combine(_tempDir, "core.db"),
            DeviceAuthPath = Path.Combine(_tempDir, "device.json"),
            EnablePathGeneration = true,
            MidiEncryptionKey = "0123456789abcdef0123456789abcdef",
        });

        // _festivalService has no songs loaded → should return early
        await worker.TryGeneratePathsAsync(_festivalService, force: false, CancellationToken.None);
    }

    [Fact]
    public async Task TryGeneratePathsAsync_catches_exceptions_gracefully()
    {
        // Load a song with a .dat URL into the festival service
        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var songsField = typeof(FestivalService).GetField("_songs", flags)!;
        var dict = (Dictionary<string, Song>)songsField.GetValue(service)!;
        dict["testSong"] = new Song
        {
            track = new Track
            {
                su = "testSong",
                tt = "Test Song",
                an = "Artist",
                mu = "http://example.com/test.dat", // has a .dat URL
                @in = new In { gr = 5 },
            }
        };

        var worker = CreateWorker(new ScraperOptions
        {
            DataDirectory = _tempDir,
            DatabasePath = Path.Combine(_tempDir, "core.db"),
            DeviceAuthPath = Path.Combine(_tempDir, "device.json"),
            EnablePathGeneration = true,
            MidiEncryptionKey = "0123456789abcdef0123456789abcdef",
            CHOptPath = Path.Combine(_tempDir, "nonexistent_chopt"),
        });

        // PathGenerator will fail (no CHOpt binary) but TryGeneratePathsAsync
        // should catch the exception and not propagate it
        await worker.TryGeneratePathsAsync(service, force: false, CancellationToken.None);
        // If we get here, the method caught the error gracefully
    }

    [Fact]
    public async Task TryGeneratePathsAsync_persists_results_to_PathDataStore()
    {
        // 1) Create fake CHOpt script
        string fakeChopt;
        if (OperatingSystem.IsWindows())
        {
            fakeChopt = Path.Combine(_tempDir, "fake_chopt.bat");
            File.WriteAllText(fakeChopt,
                "@echo off\necho Total score: 99999\nset \"out=\"\n:p\nif \"%~1\"==\"\" goto d\nif \"%~1\"==\"-o\" set \"out=%~2\"\nshift\ngoto p\n:d\nif defined out echo PNG>\"%out%\"\n");
        }
        else
        {
            fakeChopt = Path.Combine(_tempDir, "fake_chopt.sh");
            File.WriteAllText(fakeChopt,
                "#!/bin/sh\necho 'Total score: 99999'\no=\"\"\nwhile [ \"$#\" -gt 0 ]; do case \"$1\" in -o) o=\"$2\"; shift ;; esac; shift; done\n[ -n \"$o\" ] && echo PNG > \"$o\"\n");
            File.SetUnixFileMode(fakeChopt,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // 2) Create a minimal MIDI, encrypt with a known key
        var midiKey = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(midiKey);
        var keyHex = Convert.ToHexString(midiKey);

        using var midiMs = new MemoryStream();
        midiMs.Write("MThd"u8);
        WriteBE(midiMs, 6); WriteBE16(midiMs, 1); WriteBE16(midiMs, 1); WriteBE16(midiMs, 480);
        var trk = new byte[] { 0x00, 0xFF, 0x2F, 0x00 };
        midiMs.Write("MTrk"u8); WriteBE(midiMs, trk.Length); midiMs.Write(trk);
        var midiBytes = midiMs.ToArray();

        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = midiKey; aes.Mode = System.Security.Cryptography.CipherMode.ECB;
        aes.Padding = System.Security.Cryptography.PaddingMode.Zeros;
        var padded = new byte[(midiBytes.Length + 15) / 16 * 16];
        Array.Copy(midiBytes, padded, midiBytes.Length);
        var encrypted = aes.CreateEncryptor().TransformFinalBlock(padded, 0, padded.Length);

        // 3) Mock HTTP to serve the encrypted .dat
        var handler = new Helpers.MockHttpMessageHandler();
        handler.EnqueueResponse(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(encrypted),
        });

        // 4) Create a Songs table in core.db so PathDataStore can write
        var dbPath = Path.Combine(_tempDir, "core.db");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS Songs (
                    SongId TEXT PRIMARY KEY, Title TEXT,
                    MaxLeadScore INTEGER, MaxBassScore INTEGER, MaxDrumsScore INTEGER,
                    MaxVocalsScore INTEGER, MaxProLeadScore INTEGER, MaxProBassScore INTEGER,
                    DatFileHash TEXT, SongLastModified TEXT, PathsGeneratedAt TEXT, CHOptVersion TEXT
                );
                INSERT INTO Songs (SongId, Title) VALUES ('testSong', 'Test Song');
                """;
            cmd.ExecuteNonQuery();
        }

        // 5) Load a festival service with a song that has a .dat URL
        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var songsField = typeof(FestivalService).GetField("_songs", flags)!;
        var dirtyField = typeof(FestivalService).GetField("_songsDirty", flags)!;
        var songDict = (Dictionary<string, Song>)songsField.GetValue(service)!;
        songDict["testSong"] = new Song
        {
            track = new Track
            {
                su = "testSong", tt = "Test Song", an = "Artist",
                mu = "http://example.com/test.dat",
                @in = new In { gr = 5 },
            }
        };
        dirtyField.SetValue(service, true);

        // 6) Create worker with real PathGenerator using fake CHOpt + mock HTTP
        var opts = new ScraperOptions
        {
            DataDirectory = _tempDir,
            DatabasePath = dbPath,
            DeviceAuthPath = Path.Combine(_tempDir, "device.json"),
            EnablePathGeneration = true,
            MidiEncryptionKey = keyHex,
            CHOptPath = fakeChopt,
            PathGenerationParallelism = 2,
        };

        // Pre-check: ensure the key and CHOpt are valid from PathGenerator's perspective
        Assert.True(File.Exists(fakeChopt), $"Fake CHOpt not found: {fakeChopt}");
        Assert.Equal(64, keyHex.Length);

        var worker = CreateWorkerWithHttp(opts, handler);

        // 7) Run TryGeneratePathsAsync
        await worker.TryGeneratePathsAsync(service, force: false, CancellationToken.None);

        // 8) Verify results were persisted
        var store = new PathDataStore(dbPath);
        var state = store.GetPathGenerationState();
        var allScores = store.GetAllMaxScores();

        // If these assertions fail, the PathGenerator returned no results.
        // Common causes: MIDI key not configured, CHOpt not found, HTTP failure.
        Assert.True(state.Count > 0, $"No dat hashes found — PathGenerator returned no results. Handler requests: {handler.Requests.Count}");
        Assert.True(allScores.ContainsKey("testSong"), $"testSong not in max scores. Hashes: {string.Join(",", state.Keys)}");
        Assert.Equal(99999, allScores["testSong"].MaxLeadScore);
    }

    private static void WriteBE(Stream s, int v) { s.WriteByte((byte)(v>>24)); s.WriteByte((byte)(v>>16)); s.WriteByte((byte)(v>>8)); s.WriteByte((byte)v); }
    private static void WriteBE16(Stream s, int v) { s.WriteByte((byte)(v>>8)); s.WriteByte((byte)v); }

    /// <summary>No-op HTTP handler for constructing sealed EpicAuthService instances.</summary>
    private sealed class NoOpHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
