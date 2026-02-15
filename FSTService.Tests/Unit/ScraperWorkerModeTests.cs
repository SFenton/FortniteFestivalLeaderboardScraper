using System.Reflection;
using FortniteFestival.Core;
using FortniteFestival.Core.Services;
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
            Substitute.For<ILogger<GlobalLeaderboardScraper>>());

        _nameResolver = Substitute.For<AccountNameResolver>(
            new HttpClient(), _metaDb, _tokenManager,
            new ScrapeProgressTracker(),
            Substitute.For<ILogger<AccountNameResolver>>());

        _personalDbBuilder = Substitute.For<PersonalDbBuilder>(
            _persistence,
            new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null),
            _tempDir,
            Substitute.For<ILogger<PersonalDbBuilder>>());

        _backfiller = Substitute.For<ScoreBackfiller>(
            _scraper, _persistence,
            Substitute.For<ILogger<ScoreBackfiller>>());

        _backfillQueue = new BackfillQueue();

        _refresher = Substitute.For<PostScrapeRefresher>(
            _scraper, _persistence,
            Substitute.For<ILogger<PostScrapeRefresher>>());

        _historyReconstructor = Substitute.For<HistoryReconstructor>(
            _scraper, _persistence, new HttpClient(),
            Substitute.For<ILogger<HistoryReconstructor>>());

        _progress = new ScrapeProgressTracker();
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
    {
        var options = Options.Create(opts ?? new ScraperOptions
        {
            DataDirectory = _tempDir,
            DatabasePath = Path.Combine(_tempDir, "core.db"),
            DeviceAuthPath = Path.Combine(_tempDir, "device.json"),
        });

        return new ScraperWorker(
            _tokenManager, _scraper, _persistence, _nameResolver,
            _personalDbBuilder, _backfiller, _backfillQueue, _refresher,
            _historyReconstructor, _progress, options, _lifetime, _log);
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
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(5));

        _personalDbBuilder.RebuildForAccounts(
            Arg.Any<IReadOnlySet<string>>(), Arg.Any<MetaDatabase>())
            .Returns(1);

        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var worker = CreateWorker();

        await InvokePrivateAsync(worker, "RunBackfillPhaseAsync", service, CancellationToken.None);

        await _backfiller.Received(1).BackfillAccountAsync(
            "acct1", service, "token", "callerAcct", Arg.Any<CancellationToken>());
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
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
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
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("failure"));
        _backfiller.BackfillAccountAsync(
            "acct2", Arg.Any<FestivalService>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(3));

        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var worker = CreateWorker();

        // Should not throw — errors are caught per-account
        await InvokePrivateAsync(worker, "RunBackfillPhaseAsync", service, CancellationToken.None);

        // Both accounts attempted
        await _backfiller.Received(2).BackfillAccountAsync(
            Arg.Any<string>(), Arg.Any<FestivalService>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
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
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(10));

        _personalDbBuilder.RebuildForAccounts(
            Arg.Any<IReadOnlySet<string>>(), Arg.Any<MetaDatabase>())
            .Returns(1);

        var worker = CreateWorker();
        await InvokePrivateAsync(worker, "RunHistoryReconPhaseAsync", CancellationToken.None);

        await _historyReconstructor.Received(1).ReconstructAccountAsync(
            "acct1", windows, "token", "callerAcct", Arg.Any<CancellationToken>());
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
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
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
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var worker = CreateWorker();

        await InvokePrivateAsync(worker, "RunBackfillPhaseAsync", service, CancellationToken.None);

        // Both accounts should have been processed
        await _backfiller.Received(2).BackfillAccountAsync(
            Arg.Any<string>(), Arg.Any<FestivalService>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        var opts = new ScraperOptions { DataDirectory = _tempDir, DegreeOfParallelism = 2 };
        var worker = CreateWorker(opts);

        await InvokePrivateAsync(worker, "RunScrapePassAsync", service, opts, CancellationToken.None);

        // Post-scrape refresh should be called for registered users
        await _refresher.Received().RefreshAllAsync(
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Any<HashSet<(string, string, string)>>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

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
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
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
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
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
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
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

    /// <summary>No-op HTTP handler for constructing sealed EpicAuthService instances.</summary>
    private sealed class NoOpHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
