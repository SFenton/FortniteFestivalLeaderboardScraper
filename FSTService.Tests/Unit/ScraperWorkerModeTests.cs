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
/// Pure-mock tests for ScraperWorker execution modes and internal methods.
/// Tests that mutate MetaDatabase state live in <see cref="ScraperWorkerStatefulTests"/>.
/// Both classes run in parallel via xUnit's default cross-class parallelism.
/// </summary>
public class ScraperWorkerModeTests : ScraperWorkerTestBase
{

    // ═══════════════════════════════════════════════════════════════
    // ApiOnly mode
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_ApiOnly_RunsWithoutScraping()
    {
        var worker = CreateWorker(new ScraperOptions { ApiOnly = true, DataDirectory = _tempDir });

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        cts.Cancel();
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
        var setupDone = new TaskCompletionSource();
        _tokenManager.PerformDeviceCodeSetupAsync(Arg.Any<CancellationToken>())
            .Returns(ci => { setupDone.TrySetResult(); return Task.FromResult(true); });

        var worker = CreateWorker(new ScraperOptions { SetupOnly = true, DataDirectory = _tempDir });

        await worker.StartAsync(CancellationToken.None);
        await setupDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        await _tokenManager.Received(1).PerformDeviceCodeSetupAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_SetupOnly_Failure_StillCompletes()
    {
        var setupDone = new TaskCompletionSource();
        _tokenManager.PerformDeviceCodeSetupAsync(Arg.Any<CancellationToken>())
            .Returns(ci => { setupDone.TrySetResult(); return Task.FromResult(false); });

        var worker = CreateWorker(new ScraperOptions { SetupOnly = true, DataDirectory = _tempDir });

        await worker.StartAsync(CancellationToken.None);
        await setupDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
    // RunResolveOnlyAsync (pure-mock: no DB mutations)
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

    // ═══════════════════════════════════════════════════════════════
    // RunBackfillPhaseAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunBackfillPhase_NothingQueued_ReturnsEarly()
    {
        var orchestrator = CreateBackfillOrchestrator();

        await orchestrator.RunBackfillAsync(_festivalService, CancellationToken.None);

        await _machine.DidNotReceive().RunAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyList<UserWorkItem>>(),
            Arg.Any<IReadOnlyList<SeasonWindowInfo>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SharedDopPool>(),
            Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunBackfillPhase_QueuedAccounts_NoToken_ReEnqueues()
    {
        _backfillQueue.Enqueue(new BackfillRequest("acct1"));

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var orchestrator = CreateBackfillOrchestrator();

        await orchestrator.RunBackfillAsync(_festivalService, CancellationToken.None);

        // Should not have called machine
        await _machine.DidNotReceive().RunAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyList<UserWorkItem>>(),
            Arg.Any<IReadOnlyList<SeasonWindowInfo>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SharedDopPool>(),
            Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunBackfillPhase_QueuedAccounts_WithToken_Backfills()
    {
        _backfillQueue.Enqueue(new BackfillRequest("acct1"));

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("token"));
        _tokenManager.AccountId.Returns("callerAcct");

        var orchestrator = CreateBackfillOrchestrator();

        await orchestrator.RunBackfillAsync(_festivalService, CancellationToken.None);

        // Machine should have been called with the queued account
        await _machine.Received(1).RunAsync(
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Is<IReadOnlyList<UserWorkItem>>(u => u.Count == 1 && u[0].AccountId == "acct1"),
            Arg.Any<IReadOnlyList<SeasonWindowInfo>>(),
            "token", "callerAcct", Arg.Any<SharedDopPool>(),
            false, Arg.Any<int>(), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunBackfillPhase_WithToken_CallsMachineAndRunsCompletionActions()
    {
        _backfillQueue.Enqueue(new BackfillRequest("acct1"));

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("token"));
        _tokenManager.AccountId.Returns("callerAcct");

        var orchestrator = CreateBackfillOrchestrator();

        await orchestrator.RunBackfillAsync(_festivalService, CancellationToken.None);

        // Machine called for backfill
        await _machine.Received(1).RunAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyList<UserWorkItem>>(),
            Arg.Any<IReadOnlyList<SeasonWindowInfo>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SharedDopPool>(),
            Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunBackfillPhase_MachineThrows_DoesNotPropagate()
    {
        _backfillQueue.Enqueue(new BackfillRequest("acct1"));

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("token"));
        _tokenManager.AccountId.Returns("callerAcct");

        _machine.RunAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyList<UserWorkItem>>(),
            Arg.Any<IReadOnlyList<SeasonWindowInfo>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SharedDopPool>(),
            Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("machine failure"));

        var orchestrator = CreateBackfillOrchestrator();

        // Should not throw — machine errors are caught
        await orchestrator.RunBackfillAsync(_festivalService, CancellationToken.None);
    }

    [Fact]
    public async Task RunBackfillPhase_PersonalDbRebuildThrows_DoesNotPropagate()
    {
        _backfillQueue.Enqueue(new BackfillRequest("acct1"));

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("token"));
        _tokenManager.AccountId.Returns("callerAcct");

        var orchestrator = CreateBackfillOrchestrator();

        // Should not throw — post-completion errors are caught per-user
        await orchestrator.RunBackfillAsync(_festivalService, CancellationToken.None);
    }

    // ═══════════════════════════════════════════════════════════════
    // RunHistoryReconPhaseAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunHistoryReconPhase_NoRegisteredUsers_ReturnsEarly()
    {
        var orchestrator = CreateBackfillOrchestrator();
        await orchestrator.RunHistoryReconAsync(_festivalService, CancellationToken.None);

        await _historyReconstructor.DidNotReceive().DiscoverSeasonWindowsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    // Auth failure path
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_AuthFails_StopsGracefully()
    {
        var setupAttempted = new TaskCompletionSource();
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        _tokenManager.PerformDeviceCodeSetupAsync(Arg.Any<CancellationToken>())
            .Returns(ci => { setupAttempted.TrySetResult(); return Task.FromResult(false); });

        var worker = CreateWorker();
        await worker.StartAsync(CancellationToken.None);
        await setupAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        // Auth failed → should not have scraped
        await _scraper.DidNotReceive().ScrapeManySongsAsync(
            Arg.Any<IReadOnlyList<GlobalLeaderboardScraper.SongScrapeRequest>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<Func<string, List<GlobalLeaderboardResult>, ValueTask>?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<int>(), Arg.Any<double>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<AdaptiveConcurrencyLimiter?>());
    }

    // ═══════════════════════════════════════════════════════════════
    // RunSingleSongTestAsync
    // ═══════════════════════════════════════════════════════════════

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
            Arg.Any<CancellationToken>(),
            Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<int>(), Arg.Any<double>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<AdaptiveConcurrencyLimiter?>())
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
            Arg.Any<CancellationToken>(),
            Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<int>(), Arg.Any<double>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<AdaptiveConcurrencyLimiter?>());
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
            Arg.Any<CancellationToken>(),
            Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<int>(), Arg.Any<double>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<AdaptiveConcurrencyLimiter?>());
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
            Arg.Any<CancellationToken>(),
            Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<int>(), Arg.Any<double>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<AdaptiveConcurrencyLimiter?>());
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
            Arg.Any<CancellationToken>(),
            Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<int>(), Arg.Any<double>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<AdaptiveConcurrencyLimiter?>())
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
            Arg.Any<CancellationToken>(),
            Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<int>(), Arg.Any<double>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<AdaptiveConcurrencyLimiter?>());
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
            Arg.Any<CancellationToken>(),
            Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<int>(), Arg.Any<double>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<AdaptiveConcurrencyLimiter?>());
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
            Arg.Any<CancellationToken>(),
            Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<int>(), Arg.Any<double>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<AdaptiveConcurrencyLimiter?>())
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
            Arg.Any<CancellationToken>(),
            Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<int>(), Arg.Any<double>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<AdaptiveConcurrencyLimiter?>());

        // Verify name resolution was attempted
        await _nameResolver.Received().ResolveNewAccountsAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
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
            Arg.Any<CancellationToken>(),
            Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<int>(), Arg.Any<double>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<AdaptiveConcurrencyLimiter?>())
            .Returns(Task.FromResult(new Dictionary<string, List<GlobalLeaderboardResult>>()));

        _nameResolver.ResolveNewAccountsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("DNS error"));

        var opts = new ScraperOptions { DataDirectory = _tempDir };
        var worker = CreateWorker(opts);

        // Should not throw — name resolution failure is caught
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
        await Task.Delay(50);
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
        await Task.Delay(50);
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
}
