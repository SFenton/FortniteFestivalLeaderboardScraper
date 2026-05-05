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

    private RegisteredPlayerBandDiscoveryOrchestrator CreateOrchestrator(IRegisteredPlayerBandDiscoveryStrategy strategy, int maxLookupsPerAccount)
    {
        var bandPersistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>());
        var options = Options.Create(new ScraperOptions
        {
            EnableRegisteredPlayerBandDiscovery = true,
            RegisteredPlayerBandDiscoveryMaxAccountsPerPass = 10,
            RegisteredPlayerBandDiscoveryMaxLookupsPerAccount = maxLookupsPerAccount,
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