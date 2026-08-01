using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class RegisteredPlayerBandDiscoveryOrchestratorTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();

    private MetaDatabase Db => _fixture.Db;

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task RunAsync_discovers_band_for_registered_account_and_registers_exact_team()
    {
        Db.RegisterUser("web-tracker", "acct1");
        Db.UpsertSeasonWindow(14, "", "");
        var strategy = new FakeDiscoveryStrategy(new BandLeaderboardEntry
        {
            TeamKey = "acct1:acct2",
            TeamMembers = ["acct1", "acct2"],
            InstrumentCombo = "0:1",
            Score = 123456,
            Rank = 9,
            Season = 14,
            Source = "findteams",
            MemberStats =
            [
                new BandMemberStats { MemberIndex = 0, AccountId = "acct1", InstrumentId = 0, Score = 60000 },
                new BandMemberStats { MemberIndex = 1, AccountId = "acct2", InstrumentId = 1, Score = 63456 },
            ],
        });
        var orchestrator = CreateOrchestrator(strategy, maxLookupsPerAccount: 1);
        using var pool = new SharedDopPool(1, 1, 1, 100, Substitute.For<ILogger>());

        var result = await orchestrator.RunAsync(["song-a"], Db.GetSeasonWindows(), "token", "caller", pool);

        Assert.Equal(1, result.AccountsProcessed);
        Assert.Equal(1, result.LookupsChecked);
        Assert.Equal(1, result.EntriesFound);
        Assert.Equal(1, result.EntriesPersisted);
        Assert.Contains("acct1:acct2", result.ImpactedTeamsByBandType["Band_Duets"]);
        Assert.Single(strategy.Calls);
        Assert.Equal("acct1", strategy.Calls[0].AccountId);
        Assert.Equal("Band_Duets", strategy.Calls[0].Intent.BandType);
        Assert.Equal(RegisteredBandLookupScope.AllTime, strategy.Calls[0].Intent.Scope);

        var discoveryProgress = Db.GetCheckedRegisteredPlayerBandDiscoveryLookups("acct1");
        var discoveryRow = Assert.Single(discoveryProgress);
        Assert.Equal("song-a", discoveryRow.SongId);
        Assert.Equal("Band_Duets", discoveryRow.BandType);
        Assert.Equal("alltime", discoveryRow.Scope);
        Assert.True(discoveryRow.EntryFound);

        var exactProgress = Db.GetCheckedRegisteredBandLookups("web-band-tracker", "Band_Duets", "acct1:acct2");
        var exactRow = Assert.Single(exactProgress);
        Assert.Equal("song-a", exactRow.SongId);
        Assert.Equal("alltime", exactRow.Scope);
        Assert.True(exactRow.EntryFound);

        var registeredBand = Assert.Single(Db.GetRegisteredBands());
        Assert.Equal("Band_Duets", registeredBand.BandType);
        Assert.Equal("acct1:acct2", registeredBand.TeamKey);
        Assert.Contains("acct1", Db.GetRegisteredAccountIds());
        Assert.DoesNotContain("acct2", Db.GetRegisteredAccountIds());

        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT score FROM band_entries WHERE song_id = 'song-a' AND band_type = 'Band_Duets' AND team_key = 'acct1:acct2'";
        Assert.Equal(123456, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    [Fact]
    public async Task RunAsync_skips_previously_checked_discovery_intents()
    {
        Db.RegisterUser("web-tracker", "acct1");
        Db.MarkRegisteredPlayerBandDiscoveryChecked("acct1", "song-a", "Band_Duets", "alltime", 0, false);
        var strategy = new FakeDiscoveryStrategy(null);
        var orchestrator = CreateOrchestrator(strategy, maxLookupsPerAccount: 1);
        using var pool = new SharedDopPool(1, 1, 1, 100, Substitute.For<ILogger>());

        var result = await orchestrator.RunAsync(["song-a"], [], "token", "caller", pool);

        Assert.Equal(1, result.AccountsProcessed);
        Assert.Equal(1, result.LookupsChecked);
        Assert.Single(strategy.Calls);
        Assert.Equal("Band_Trios", strategy.Calls[0].Intent.BandType);
    }

    [Fact]
    public async Task RunAsync_NoncanonicalWindow_InvalidatesConventionalDiscoveryProgress()
    {
        const string accountId = "acct1";
        const string windowId = "season_14_competitive";
        Db.RegisterUser("web-tracker", accountId);
        foreach (var bandType in new[] { "Band_Duets", "Band_Trios", "Band_Quad" })
        {
            Db.MarkRegisteredPlayerBandDiscoveryChecked(
                accountId,
                "song-a",
                bandType,
                "alltime",
                0,
                false);
        }
        Db.MarkRegisteredPlayerBandDiscoveryChecked(
            accountId,
            "song-a",
            "Band_Duets",
            "season",
            14,
            false,
            "season014");

        var strategy = new FakeDiscoveryStrategy(null);
        var orchestrator = CreateOrchestrator(strategy, maxLookupsPerAccount: 1);
        using var pool = new SharedDopPool(1, 1, 1, 100, Substitute.For<ILogger>());

        var result = await orchestrator.RunAsync(
            ["song-a"],
            [
                new SeasonWindowInfo
                {
                    SeasonNumber = 14,
                    EventId = "season14-event",
                    WindowId = windowId,
                },
            ],
            "token",
            "caller",
            pool);

        Assert.Equal(1, result.LookupsChecked);
        var call = Assert.Single(strategy.Calls);
        Assert.Equal("Band_Duets", call.Intent.BandType);
        Assert.Equal(RegisteredBandLookupScope.Season, call.Intent.Scope);
        Assert.Equal(windowId, call.Intent.WindowId);

        var progress = Db.GetCheckedRegisteredPlayerBandDiscoveryLookups(accountId);
        Assert.Equal(
            windowId,
            Assert.Single(progress, row =>
                row.BandType == "Band_Duets" &&
                row.Scope == "season").WindowId);
    }

    [Fact]
    public async Task DirectDiscoveryStrategy_sends_exact_noncanonical_window_id()
    {
        const string windowId = "season_14_competitive";
        var scraper = Substitute.For<ILeaderboardQuerier>();
        scraper.FindBandsForAccountAsync(
            "song-a",
            "Band_Duets",
            "acct1",
            windowId,
            "token",
            "caller",
            Arg.Any<AdaptiveConcurrencyLimiter?>(),
            Arg.Any<CancellationToken>())
            .Returns([]);
        var strategy = new DirectRegisteredPlayerBandDiscoveryStrategy(scraper);
        var intent = new RegisteredPlayerBandDiscoveryIntent(
            "song-a",
            "Band_Duets",
            RegisteredBandLookupScope.Season,
            14,
            windowId);

        await strategy.FetchAsync(
            "acct1",
            intent,
            "token",
            "caller",
            limiter: null,
            CancellationToken.None);

        await scraper.Received(1).FindBandsForAccountAsync(
            "song-a",
            "Band_Duets",
            "acct1",
            windowId,
            "token",
            "caller",
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ResumesWithLeastRecentlyProcessedAccountWithinPassBudget()
    {
        Db.RegisterUser("device-1", "acct1");
        Db.RegisterUser("device-2", "acct2");
        var strategy = new FakeDiscoveryStrategy(null);
        var orchestrator = CreateOrchestrator(
            strategy,
            maxLookupsPerAccount: 2,
            maxLookupsPerPass: 2);
        using var pool = new SharedDopPool(1, 1, 1, 100, Substitute.For<ILogger>());

        var first = await orchestrator.RunAsync(["song-a"], [], "token", "caller", pool);
        var second = await orchestrator.RunAsync(["song-a"], [], "token", "caller", pool);

        Assert.Equal(2, first.LookupsChecked);
        Assert.Equal(2, second.LookupsChecked);
        Assert.Equal(["acct1", "acct1", "acct2", "acct2"], strategy.Calls.Select(call => call.AccountId));
        Assert.Equal(2, Db.GetCheckedRegisteredPlayerBandDiscoveryLookups("acct1").Count);
        Assert.Equal(2, Db.GetCheckedRegisteredPlayerBandDiscoveryLookups("acct2").Count);
    }

    private RegisteredPlayerBandDiscoveryOrchestrator CreateOrchestrator(
        IRegisteredPlayerBandDiscoveryStrategy strategy,
        int maxLookupsPerAccount,
        int maxLookupsPerPass = 80)
    {
        var bandPersistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>());
        var options = Options.Create(new ScraperOptions
        {
            EnableRegisteredPlayerBandDiscovery = true,
            RegisteredPlayerBandDiscoveryMaxAccountsPerPass = 10,
            RegisteredPlayerBandDiscoveryMaxLookupsPerAccount = maxLookupsPerAccount,
            RegisteredPlayerBandDiscoveryMaxLookupsPerPass = maxLookupsPerPass,
        });

        return new RegisteredPlayerBandDiscoveryOrchestrator(
            Db,
            bandPersistence,
            strategy,
            new ScrapeProgressTracker(),
            options,
            Substitute.For<ILogger<RegisteredPlayerBandDiscoveryOrchestrator>>());
    }

    private sealed class FakeDiscoveryStrategy : IRegisteredPlayerBandDiscoveryStrategy
    {
        private readonly BandLeaderboardEntry? _entry;

        public FakeDiscoveryStrategy(BandLeaderboardEntry? entry)
        {
            _entry = entry;
        }

        public List<(string AccountId, RegisteredPlayerBandDiscoveryIntent Intent)> Calls { get; } = [];

        public Task<RegisteredPlayerBandDiscoveryLookupResult> FetchAsync(
            string accountId,
            RegisteredPlayerBandDiscoveryIntent intent,
            string accessToken,
            string callerAccountId,
            AdaptiveConcurrencyLimiter? limiter,
            CancellationToken ct)
        {
            Calls.Add((accountId, intent));

            if (_entry is not null && intent.Scope == RegisteredBandLookupScope.AllTime && intent.BandType == "Band_Duets")
                return Task.FromResult(new RegisteredPlayerBandDiscoveryLookupResult([_entry]));

            return Task.FromResult(RegisteredPlayerBandDiscoveryLookupResult.Empty);
        }
    }
}