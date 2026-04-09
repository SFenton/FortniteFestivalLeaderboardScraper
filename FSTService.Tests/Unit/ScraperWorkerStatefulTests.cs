using System.Reflection;
using FortniteFestival.Core;
using FortniteFestival.Core.Services;
using FSTService.Persistence;
using FSTService.Scraping;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FSTService.Tests.Unit;

/// <summary>
/// ScraperWorker tests that mutate MetaDatabase state (register users, backfill
/// state, account IDs). Separated from pure-mock tests to enable xUnit cross-class
/// parallelism.
/// </summary>
public class ScraperWorkerStatefulTests : ScraperWorkerTestBase
{
    // ═══════════════════════════════════════════════════════════════
    // RunResolveOnlyAsync (DB-mutating: InsertAccountIds)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunResolveOnly_WithUnresolved_CallsResolver()
    {
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
        await InvokePrivateAsync(worker, "RunResolveOnlyAsync", CancellationToken.None);
    }

    // ═══════════════════════════════════════════════════════════════
    // ExecuteAsync — ResolveOnly (DB-mutating)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_ResolveOnly_CallsResolver()
    {
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("token"));

        _metaDb.InsertAccountIds(new[] { "acct1" });

        var resolved = new TaskCompletionSource();
        _nameResolver.ResolveNewAccountsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => { resolved.TrySetResult(); return Task.FromResult(1); });

        var worker = CreateWorker(new ScraperOptions
        {
            ResolveOnly = true,
            DataDirectory = _tempDir,
        });

        await worker.StartAsync(CancellationToken.None);
        await resolved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        await _nameResolver.Received().ResolveNewAccountsAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    // RunBackfillPhaseAsync (DB-mutating: EnqueueBackfill)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunBackfillPhase_PendingInDb_MergesWithQueue()
    {
        _backfillQueue.Enqueue(new BackfillRequest("acctQ"));
        _metaDb.EnqueueBackfill("acctDb", 10);

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("token"));
        _tokenManager.AccountId.Returns("callerAcct");

        var orchestrator = CreateBackfillOrchestrator();
        await orchestrator.RunBackfillAsync(_festivalService, CancellationToken.None);

        // Machine should have been called with both accounts merged
        await _cyclicalMachine.Received(1).AttachAsync(
            Arg.Is<IReadOnlyList<UserWorkItem>>(u => u.Count == 2),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<IReadOnlyList<SeasonWindowInfo>>(),
            Arg.Any<SongMachineSource>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    // RunHistoryReconPhaseAsync (DB-mutating)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunHistoryReconPhase_RegisteredButNoCompletedBackfill_ReturnsEarly()
    {
        _metaDb.RegisterUser("dev1", "acct1");

        var orchestrator = CreateBackfillOrchestrator();
        await orchestrator.RunHistoryReconAsync(_festivalService, CancellationToken.None);

        await _historyReconstructor.DidNotReceive().DiscoverSeasonWindowsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunHistoryReconPhase_CompletedBackfill_NoToken_Skips()
    {
        _metaDb.RegisterUser("dev1", "acct1");
        _metaDb.EnqueueBackfill("acct1", 10);
        _metaDb.StartBackfill("acct1");
        _metaDb.CompleteBackfill("acct1");

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var orchestrator = CreateBackfillOrchestrator();
        await orchestrator.RunHistoryReconAsync(_festivalService, CancellationToken.None);

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

        var orchestrator = CreateBackfillOrchestrator();
        await orchestrator.RunHistoryReconAsync(_festivalService, CancellationToken.None);

        // Machine should have been called with the user needing history recon
        await _cyclicalMachine.Received(1).AttachAsync(
            Arg.Is<IReadOnlyList<UserWorkItem>>(u => u.Count == 1 && u[0].AccountId == "acct1"
                && u[0].Purposes.HasFlag(WorkPurpose.HistoryRecon)),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<IReadOnlyList<SeasonWindowInfo>>(),
            Arg.Any<SongMachineSource>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
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

        var orchestrator = CreateBackfillOrchestrator();
        await orchestrator.RunHistoryReconAsync(_festivalService, CancellationToken.None);

        await _historyReconstructor.DidNotReceive().ReconstructAccountAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<SeasonWindowInfo>>(),
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<SharedDopPool>(), Arg.Any<int>(),
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

        var orchestrator = CreateBackfillOrchestrator();
        await orchestrator.RunHistoryReconAsync(_festivalService, CancellationToken.None);
    }

    [Fact]
    public async Task RunHistoryReconPhase_MachineThrows_DoesNotPropagate()
    {
        _metaDb.RegisterUser("dev1", "acct1");
        _metaDb.EnqueueBackfill("acct1", 10);
        _metaDb.StartBackfill("acct1");
        _metaDb.CompleteBackfill("acct1");
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

        _cyclicalMachine.AttachAsync(
            Arg.Any<IReadOnlyList<UserWorkItem>>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<IReadOnlyList<SeasonWindowInfo>>(),
            Arg.Any<SongMachineSource>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("machine error"));

        var orchestrator = CreateBackfillOrchestrator();

        // Should not propagate — machine errors are caught
        await orchestrator.RunHistoryReconAsync(_festivalService, CancellationToken.None);
    }

    [Fact]
    public async Task RunHistoryReconPhase_AlreadyReconstructed_Skips()
    {
        _metaDb.RegisterUser("dev1", "acct1");
        _metaDb.EnqueueBackfill("acct1", 10);
        _metaDb.StartBackfill("acct1");
        _metaDb.CompleteBackfill("acct1");
        _metaDb.EnqueueHistoryRecon("acct1", 10);
        _metaDb.StartHistoryRecon("acct1");
        _metaDb.CompleteHistoryRecon("acct1");

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("token"));
        _tokenManager.AccountId.Returns("callerAcct");

        var orchestrator = CreateBackfillOrchestrator();
        await orchestrator.RunHistoryReconAsync(_festivalService, CancellationToken.None);

        await _historyReconstructor.DidNotReceive().DiscoverSeasonWindowsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    // RunScrapePassAsync (DB-mutating: registered users, callbacks)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunScrapePass_WithRegisteredUsers_RefreshesAndBackfills()
    {
        var service = CreateServiceWithSongs(("s1", "Song One", "Artist"));

        _metaDb.RegisterUser("dev1", "regAcct");

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("token"));
        _tokenManager.AccountId.Returns("callerAcct");

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
            Arg.Any<CancellationToken>(),
            Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<int>(), Arg.Any<double>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<AdaptiveConcurrencyLimiter?>(), Arg.Any<bool>(),
            Arg.Any<double>(), Arg.Any<Func<string, string, IReadOnlyList<LeaderboardEntry>, ValueTask>?>(),
            Arg.Any<Func<string, string, IReadOnlyList<BandLeaderboardEntry>, ValueTask>?>())
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

        _refresher.RefreshAllAsync(
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Any<HashSet<(string, string, string)>>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<FortniteFestival.Core.Scraping.AdaptiveConcurrencyLimiter>(), Arg.Any<int>(), Arg.Any<int>(), ct: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        var opts = new ScraperOptions { DataDirectory = _tempDir, DegreeOfParallelism = 2 };
        var worker = CreateWorker(opts);

        await InvokePrivateAsync(worker, "RunScrapePassAsync", service, opts, CancellationToken.None);

        await _cyclicalMachine.Received().AttachAsync(
            Arg.Any<IReadOnlyList<UserWorkItem>>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<IReadOnlyList<FSTService.Persistence.SeasonWindowInfo>>(),
            Arg.Any<SongMachineSource>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
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
            Arg.Any<CancellationToken>(),
            Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<int>(), Arg.Any<double>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<AdaptiveConcurrencyLimiter?>(), Arg.Any<bool>(),
            Arg.Any<double>(), Arg.Any<Func<string, string, IReadOnlyList<LeaderboardEntry>, ValueTask>?>(),
            Arg.Any<Func<string, string, IReadOnlyList<BandLeaderboardEntry>, ValueTask>?>())
            .Returns(Task.FromResult(new Dictionary<string, List<GlobalLeaderboardResult>>()));

        _nameResolver.ResolveNewAccountsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        _refresher.RefreshAllAsync(
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Any<HashSet<(string, string, string)>>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<FortniteFestival.Core.Scraping.AdaptiveConcurrencyLimiter>(), Arg.Any<int>(), Arg.Any<int>(), ct: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(5));

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
            Arg.Any<CancellationToken>(),
            Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<int>(), Arg.Any<double>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<AdaptiveConcurrencyLimiter?>(), Arg.Any<bool>(),
            Arg.Any<double>(), Arg.Any<Func<string, string, IReadOnlyList<LeaderboardEntry>, ValueTask>?>(),
            Arg.Any<Func<string, string, IReadOnlyList<BandLeaderboardEntry>, ValueTask>?>())
            .Returns(Task.FromResult(new Dictionary<string, List<GlobalLeaderboardResult>>()));

        _nameResolver.ResolveNewAccountsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        _refresher.RefreshAllAsync(
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Any<HashSet<(string, string, string)>>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<FortniteFestival.Core.Scraping.AdaptiveConcurrencyLimiter>(), Arg.Any<int>(), Arg.Any<int>(), ct: Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Refresh crashed"));

        var opts = new ScraperOptions { DataDirectory = _tempDir, DegreeOfParallelism = 2 };
        var worker = CreateWorker(opts);

        await InvokePrivateAsync(worker, "RunScrapePassAsync", service, opts, CancellationToken.None);
    }

    [Fact]
    public async Task RunScrapePass_RefreshTokenNullForPostScrape_LogsWarning()
    {
        var service = CreateServiceWithSongs(("s1", "Song One", "Artist"));

        _metaDb.RegisterUser("dev1", "regAcct");

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<string?>("token"),
                Task.FromResult<string?>(null));
        _tokenManager.AccountId.Returns("callerAcct");

        _scraper.ScrapeManySongsAsync(
            Arg.Any<IReadOnlyList<GlobalLeaderboardScraper.SongScrapeRequest>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<Func<string, List<GlobalLeaderboardResult>, ValueTask>?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<int>(), Arg.Any<double>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<AdaptiveConcurrencyLimiter?>(), Arg.Any<bool>(),
            Arg.Any<double>(), Arg.Any<Func<string, string, IReadOnlyList<LeaderboardEntry>, ValueTask>?>(),
            Arg.Any<Func<string, string, IReadOnlyList<BandLeaderboardEntry>, ValueTask>?>())
            .Returns(Task.FromResult(new Dictionary<string, List<GlobalLeaderboardResult>>()));

        _nameResolver.ResolveNewAccountsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        var opts = new ScraperOptions { DataDirectory = _tempDir, DegreeOfParallelism = 2 };
        var worker = CreateWorker(opts);

        await InvokePrivateAsync(worker, "RunScrapePassAsync", service, opts, CancellationToken.None);
    }

    // ═══════════════════════════════════════════════════════════════
    // TryGeneratePathsAsync (DB-mutating: writes to core.db)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task TryGeneratePathsAsync_persists_results_to_PathDataStore()
    {
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

        var handler = new Helpers.MockHttpMessageHandler();
        handler.EnqueueResponse(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(encrypted),
        });

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

        var opts = new ScraperOptions
        {
            DataDirectory = _tempDir,
            DeviceAuthPath = Path.Combine(_tempDir, "device.json"),
            EnablePathGeneration = true,
            MidiEncryptionKey = keyHex,
            CHOptPath = fakeChopt,
            PathGenerationParallelism = 2,
        };

        Assert.True(File.Exists(fakeChopt), $"Fake CHOpt not found: {fakeChopt}");
        Assert.Equal(64, keyHex.Length);

        var worker = CreateWorkerWithHttp(opts, handler);

        // Ensure the song row exists in PG so UpdateMaxScores can UPDATE it
        var storeField = typeof(ScraperWorker).GetField("_pathDataStore",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var store = (PathDataStore)storeField.GetValue(worker)!;
        var dsField = typeof(PathDataStore).GetField("_ds",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var ds = (Npgsql.NpgsqlDataSource)dsField.GetValue(store)!;
        using (var seedConn = ds.OpenConnection())
        {
            using var seedCmd = seedConn.CreateCommand();
            seedCmd.CommandText = "INSERT INTO songs (song_id, title) VALUES ('testSong', 'Test Song') ON CONFLICT DO NOTHING";
            seedCmd.ExecuteNonQuery();
        }

        await worker.TryGeneratePathsAsync(service, force: false, CancellationToken.None);

        // Verify path generation ran by checking the worker's PathDataStore via reflection
        var state = store.GetPathGenerationState();
        var allScores = store.GetAllMaxScores();

        Assert.True(state.Count > 0, $"No dat hashes found — PathGenerator returned no results. Handler requests: {handler.Requests.Count}");
        Assert.True(allScores.ContainsKey("testSong"), $"testSong not in max scores. Hashes: {string.Join(",", state.Keys)}");
        Assert.Equal(99999, allScores["testSong"].MaxLeadScore);
    }
}
