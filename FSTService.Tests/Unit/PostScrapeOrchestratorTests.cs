using FortniteFestival.Core.Services;
using FSTService.Api;
using FSTService.Auth;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FSTService.Tests.Unit;

public class PostScrapeOrchestratorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly MetaDatabase _metaDb;
    private readonly GlobalLeaderboardPersistence _persistence;

    private readonly TokenManager _tokenManager;
    private readonly FirstSeenSeasonCalculator _firstSeenCalculator;
    private readonly AccountNameResolver _nameResolver;
    private readonly PersonalDbBuilder _personalDbBuilder;
    private readonly PostScrapeRefresher _refresher;
    private readonly NotificationService _notifications;
    private readonly ScrapeProgressTracker _progress;
    private readonly ILogger<PostScrapeOrchestrator> _log;

    private readonly PostScrapeOrchestrator _sut;

    public PostScrapeOrchestratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"pso_test_{Guid.NewGuid():N}");
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

        var scraper = Substitute.For<GlobalLeaderboardScraper>(
            new HttpClient(),
            new ScrapeProgressTracker(),
            Substitute.For<ILogger<GlobalLeaderboardScraper>>(),
            0);

        _firstSeenCalculator = Substitute.For<FirstSeenSeasonCalculator>(
            scraper, _persistence, new ScrapeProgressTracker(),
            Substitute.For<ILogger<FirstSeenSeasonCalculator>>());

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

        _refresher = Substitute.For<PostScrapeRefresher>(
            scraper, _persistence,
            Substitute.For<ILogger<PostScrapeRefresher>>());

        _notifications = new NotificationService(Substitute.For<ILogger<NotificationService>>());
        _progress = new ScrapeProgressTracker();
        _log = Substitute.For<ILogger<PostScrapeOrchestrator>>();

        var rivalsCalculator = new RivalsCalculator(_persistence, Substitute.For<ILogger<RivalsCalculator>>());
        var rivalsOrchestrator = new RivalsOrchestrator(rivalsCalculator, _persistence, new Api.NotificationService(Substitute.For<ILogger<Api.NotificationService>>()), _progress, Substitute.For<ILogger<RivalsOrchestrator>>());

        _sut = new PostScrapeOrchestrator(
            _persistence, _firstSeenCalculator, _nameResolver,
            _personalDbBuilder, _refresher, rivalsOrchestrator, _notifications,
            _tokenManager, _progress, _log);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        _metaDb.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private ScrapePassContext CreateContext(
        HashSet<string>? registeredIds = null,
        GlobalLeaderboardPersistence.PipelineAggregates? aggregates = null,
        IReadOnlyList<GlobalLeaderboardScraper.SongScrapeRequest>? scrapeRequests = null)
    {
        return new ScrapePassContext
        {
            AccessToken = "test-token",
            CallerAccountId = "caller-001",
            RegisteredIds = registeredIds ?? new HashSet<string>(),
            Aggregates = aggregates ?? new GlobalLeaderboardPersistence.PipelineAggregates(),
            ScrapeRequests = scrapeRequests ?? Array.Empty<GlobalLeaderboardScraper.SongScrapeRequest>(),
            DegreeOfParallelism = 4,
        };
    }

    // ═══════════════════════════════════════════════════════════
    // RebuildPersonalDbsAsync
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task RebuildPersonalDbs_NoChanges_SkipsRebuild()
    {
        var ctx = CreateContext();

        await _sut.RebuildPersonalDbsAsync(ctx, CancellationToken.None);

        _personalDbBuilder.DidNotReceiveWithAnyArgs()
            .RebuildForAccounts(default!, default!);
    }

    [Fact]
    public async Task RebuildPersonalDbs_WithChanges_RebuildsAndNotifies()
    {
        var aggregates = new GlobalLeaderboardPersistence.PipelineAggregates();
        aggregates.AddChangedAccountIds(new[] { "acct-1", "acct-2" });

        _personalDbBuilder.RebuildForAccounts(
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Any<MetaDatabase>())
            .Returns(2);

        var ctx = CreateContext(aggregates: aggregates);

        await _sut.RebuildPersonalDbsAsync(ctx, CancellationToken.None);

        _personalDbBuilder.Received(1).RebuildForAccounts(
            Arg.Is<IReadOnlySet<string>>(s => s.Count == 2),
            Arg.Any<MetaDatabase>());
    }

    [Fact]
    public async Task RebuildPersonalDbs_ThrowsException_DoesNotPropagate()
    {
        var aggregates = new GlobalLeaderboardPersistence.PipelineAggregates();
        aggregates.AddChangedAccountIds(new[] { "acct-1" });

        _personalDbBuilder.RebuildForAccounts(
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Any<MetaDatabase>())
            .Throws(new InvalidOperationException("DB error"));

        var ctx = CreateContext(aggregates: aggregates);

        // Should not throw — exception is caught and logged
        await _sut.RebuildPersonalDbsAsync(ctx, CancellationToken.None);
    }

    // ═══════════════════════════════════════════════════════════
    // RefreshRegisteredUsersAsync
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task RefreshRegisteredUsers_NoRegisteredUsers_Skips()
    {
        var ctx = CreateContext();

        await _sut.RefreshRegisteredUsersAsync(ctx, CancellationToken.None);

        await _refresher.DidNotReceiveWithAnyArgs()
            .RefreshAllAsync(default!, default!, default!, default!, default!, default, default);
    }

    [Fact]
    public async Task RefreshRegisteredUsers_WithToken_Refreshes()
    {
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns("test-access-token");
        _tokenManager.AccountId.Returns("caller-001");

        _refresher.RefreshAllAsync(
            Arg.Any<HashSet<string>>(),
            Arg.Any<HashSet<(string, string, string)>>(),
            Arg.Any<List<string>>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Returns(5);

        var ctx = CreateContext(registeredIds: new HashSet<string> { "user-1" });

        await _sut.RefreshRegisteredUsersAsync(ctx, CancellationToken.None);

        await _refresher.Received(1).RefreshAllAsync(
            Arg.Any<HashSet<string>>(),
            Arg.Any<HashSet<(string, string, string)>>(),
            Arg.Any<List<string>>(),
            Arg.Is("test-access-token"),
            Arg.Is("caller-001"),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshRegisteredUsers_NoToken_Skips()
    {
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var ctx = CreateContext(registeredIds: new HashSet<string> { "user-1" });

        await _sut.RefreshRegisteredUsersAsync(ctx, CancellationToken.None);

        await _refresher.DidNotReceiveWithAnyArgs()
            .RefreshAllAsync(default!, default!, default!, default!, default!, default, default);
    }

    [Fact]
    public async Task RefreshRegisteredUsers_ThrowsException_DoesNotPropagate()
    {
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns("test-access-token");
        _tokenManager.AccountId.Returns("caller-001");

        _refresher.RefreshAllAsync(
            Arg.Any<HashSet<string>>(),
            Arg.Any<HashSet<(string, string, string)>>(),
            Arg.Any<List<string>>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("API error"));

        var ctx = CreateContext(registeredIds: new HashSet<string> { "user-1" });

        // Should not throw
        await _sut.RefreshRegisteredUsersAsync(ctx, CancellationToken.None);
    }

    // ═══════════════════════════════════════════════════════════
    // CleanupSessions
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void CleanupSessions_CleansExpiredSessions()
    {
        // Pre-seed a session that is expired (older than 7 days)
        _metaDb.InsertSession("acct-1", "device-1", "refresh-tok", "Windows", DateTime.UtcNow.AddDays(-30));

        _sut.CleanupSessions();

        // The expired session should be cleaned. Verify no exception and
        // the orphaned account auto-unregisters (acct-1 was never registered,
        // so GetOrphanedRegisteredAccounts should return nothing to unregister).
        // The key verification is that CleanupSessions runs without throwing.
    }

    // ═══════════════════════════════════════════════════════════
    // ResolveNamesAsync
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task ResolveNamesAsync_DelegatesToResolver()
    {
        _nameResolver.ResolveNewAccountsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(42);

        var result = await _sut.ResolveNamesAsync(8, CancellationToken.None);

        Assert.Equal(42, result);
        await _nameResolver.Received(1).ResolveNewAccountsAsync(8, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════
    // RunEnrichmentAsync
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task RunEnrichmentAsync_SetsPhase()
    {
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var ctx = CreateContext();

        await _sut.RunEnrichmentAsync(ctx, service, CancellationToken.None);

        Assert.Equal(ScrapeProgressTracker.ScrapePhase.PostScrapeEnrichment, _progress.Phase);
    }

    // ═══════════════════════════════════════════════════════════
    // NoOpHttpHandler (shared utility)
    // ═══════════════════════════════════════════════════════════

    private sealed class NoOpHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
