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

    [Fact]
    public async Task RunAsync_zero_lookup_account_consumes_attempt_budget()
    {
        Db.RegisterUser("web-tracker", "acct1");
        foreach (var bandType in new[] { "Band_Duets", "Band_Trios", "Band_Quad" })
        {
            Db.MarkRegisteredPlayerBandDiscoveryChecked(
                "acct1",
                "song-a",
                bandType,
                "alltime",
                0,
                false);
        }
        var tracker = new ScrapeProgressTracker();
        tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.SongMachine);
        tracker.SetSubOperation("registered_player_band_discovery");
        var orchestrator = CreateOrchestrator(
            new FakeDiscoveryStrategy(null),
            maxLookupsPerAccount: 10,
            maxAccountsPerPass: 1,
            tracker: tracker);
        using var pool = new SharedDopPool(1, 1, 1, 100, Substitute.For<ILogger>());

        var result = await orchestrator.RunAsync(
            ["song-a"],
            [],
            "token",
            "caller",
            pool);

        Assert.Equal(1, result.AccountsProcessed);
        Assert.Equal(0, result.LookupsChecked);
        Assert.Equal(1, tracker.GetProgressResponse().Current?.Accounts?.Completed);
    }

    [Fact]
    public async Task RunAsync_invalid_leaderboard_remains_retryable_then_succeeds()
    {
        Db.RegisterUser("web-tracker", "acct1");
        var strategy = new InvalidThenSuccessDiscoveryStrategy();
        var orchestrator = CreateOrchestrator(
            strategy,
            maxLookupsPerAccount: 1);
        using var pool = new SharedDopPool(1, 1, 1, 100, Substitute.For<ILogger>());

        var first = await orchestrator.RunAsync(
            ["song-a"],
            [],
            "token",
            "caller",
            pool);

        Assert.Equal(0, first.LookupsChecked);
        Assert.Empty(Db.GetCheckedRegisteredPlayerBandDiscoveryLookups("acct1"));
        using (var conn = _fixture.DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT checked
                FROM registered_player_band_discovery_progress
                WHERE account_id = 'acct1'
                """;
            Assert.Equal(0, Convert.ToInt32(cmd.ExecuteScalar()));
        }

        var second = await orchestrator.RunAsync(
            ["song-a"],
            [],
            "token",
            "caller",
            pool);

        Assert.Equal(1, second.LookupsChecked);
        Assert.Equal(1, second.EntriesPersisted);
        Assert.Single(Db.GetCheckedRegisteredPlayerBandDiscoveryLookups("acct1"));
    }

    [Fact]
    public async Task RunAsync_progress_counts_only_77_successful_durable_lookups()
    {
        Db.RegisterUser("web-tracker", "acct1");
        var tracker = new ScrapeProgressTracker();
        tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.SongMachine);
        tracker.SetSubOperation("registered_player_band_discovery");
        var strategy = new FailAfterDiscoveryStrategy(77);
        var orchestrator = CreateOrchestrator(
            strategy,
            maxLookupsPerAccount: 80,
            maxLookupsPerPass: 80,
            tracker: tracker);
        using var pool = new SharedDopPool(1, 1, 1, 100, Substitute.For<ILogger>());

        var result = await orchestrator.RunAsync(
            Enumerable.Range(0, 80).Select(index => $"song-{index}").ToArray(),
            [],
            "token",
            "caller",
            pool);

        Assert.Equal(77, result.LookupsChecked);
        Assert.Equal(77, tracker.GetProgressResponse().Current?.WorkItems?.Completed);
        Assert.Equal(80, tracker.GetProgressResponse().Current?.WorkItems?.Total);
        Assert.Null(tracker.GetProgressResponse().Current?.CurrentDop);
    }

    [Fact]
    public async Task RunAsync_cancellation_preserves_partial_impacts_and_clears_limiter()
    {
        Db.RegisterUser("web-tracker", "acct1");
        var tracker = new ScrapeProgressTracker();
        var orchestrator = CreateOrchestrator(
            new SuccessThenCancelDiscoveryStrategy(),
            maxLookupsPerAccount: 2,
            tracker: tracker);
        using var pool = new SharedDopPool(1, 1, 1, 100, Substitute.For<ILogger>());

        var exception = await Assert.ThrowsAsync<
            PartialResultOperationCanceledException<RegisteredPlayerBandDiscoveryResult>>(
            () => orchestrator.RunAsync(
                ["song-a"],
                [],
                "token",
                "caller",
                pool));

        Assert.Equal(1, exception.PartialResult.LookupsChecked);
        Assert.Contains(
            "acct1:acct2",
            exception.PartialResult.ImpactedTeamsByBandType["Band_Duets"]);
        Assert.NotEmpty(exception.PartialResult.ImpactedCurrentProjectionScopes);
        Assert.Null(tracker.GetProgressResponse().Current?.CurrentDop);
    }

    [Fact]
    public async Task RunAsync_metadata_failure_preserves_persisted_impacts_without_completing_lookup()
    {
        Db.RegisterUser("web-tracker", "acct1");
        using (var connection = _fixture.DataSource.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE OR REPLACE FUNCTION fail_discovered_band_metadata()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'synthetic discovered-band metadata failure';
                END;
                $$;

                CREATE TRIGGER trg_fail_discovered_band_metadata
                BEFORE INSERT OR UPDATE ON registered_bands
                FOR EACH ROW
                EXECUTE FUNCTION fail_discovered_band_metadata();
                """;
            command.ExecuteNonQuery();
        }

        var strategy = new FakeDiscoveryStrategy(new BandLeaderboardEntry
        {
            TeamKey = "acct1:acct2",
            TeamMembers = ["acct1", "acct2"],
            InstrumentCombo = "0:1",
            Score = 100,
            MemberStats =
            [
                new BandMemberStats
                {
                    MemberIndex = 0,
                    AccountId = "acct1",
                    InstrumentId = 0,
                    Score = 50,
                },
                new BandMemberStats
                {
                    MemberIndex = 1,
                    AccountId = "acct2",
                    InstrumentId = 1,
                    Score = 50,
                },
            ],
        });
        var tracker = new ScrapeProgressTracker();
        tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.SongMachine);
        tracker.SetSubOperation("registered_player_band_discovery");
        var orchestrator = CreateOrchestrator(
            strategy,
            maxLookupsPerAccount: 1,
            tracker: tracker);
        using var pool = new SharedDopPool(1, 1, 1, 100, Substitute.For<ILogger>());

        var failure = await Assert.ThrowsAsync<
            PartialResultFailureException<RegisteredPlayerBandDiscoveryResult>>(
            () => orchestrator.RunAsync(
                ["song-a"],
                [],
                "token",
                "caller",
                pool));

        Assert.Contains(
            "acct1:acct2",
            failure.PartialResultValue.ImpactedTeamsByBandType["Band_Duets"]);
        Assert.NotEmpty(
            failure.PartialResultValue.ImpactedCurrentProjectionScopes);
        Assert.Equal(1, failure.PartialResultValue.EntriesPersisted);
        Assert.Equal(0, failure.PartialResultValue.LookupsChecked);
        Assert.Equal(
            0,
            tracker.GetProgressResponse().Current?.WorkItems?.Completed);
        Assert.Empty(
            Db.GetCheckedRegisteredPlayerBandDiscoveryLookups("acct1"));

        using var readConnection = _fixture.DataSource.OpenConnection();
        using var readCommand = readConnection.CreateCommand();
        readCommand.CommandText = """
            SELECT COUNT(*)
            FROM band_entries
            WHERE song_id = 'song-a'
              AND band_type = 'Band_Duets'
              AND team_key = 'acct1:acct2'
            """;
        Assert.Equal(1L, (long)readCommand.ExecuteScalar()!);
    }

    private RegisteredPlayerBandDiscoveryOrchestrator CreateOrchestrator(
        IRegisteredPlayerBandDiscoveryStrategy strategy,
        int maxLookupsPerAccount,
        int maxLookupsPerPass = 80,
        int maxAccountsPerPass = 10,
        ScrapeProgressTracker? tracker = null)
    {
        var bandPersistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>());
        var options = Options.Create(new ScraperOptions
        {
            EnableRegisteredPlayerBandDiscovery = true,
            RegisteredPlayerBandDiscoveryMaxAccountsPerPass = maxAccountsPerPass,
            RegisteredPlayerBandDiscoveryMaxLookupsPerAccount = maxLookupsPerAccount,
            RegisteredPlayerBandDiscoveryMaxLookupsPerPass = maxLookupsPerPass,
        });

        return new RegisteredPlayerBandDiscoveryOrchestrator(
            Db,
            bandPersistence,
            strategy,
            tracker ?? new ScrapeProgressTracker(),
            options,
            Substitute.For<ILogger<RegisteredPlayerBandDiscoveryOrchestrator>>(),
            new RegistrationMutationCoordinator(
                Db,
                Substitute.For<IPathDataStore>(),
                Substitute.For<
                    ISongInstrumentSupportCache>()));
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

    private sealed class InvalidThenSuccessDiscoveryStrategy
            : IRegisteredPlayerBandDiscoveryStrategy
    {
        private int _calls;

        public Task<RegisteredPlayerBandDiscoveryLookupResult> FetchAsync(
            string accountId,
            RegisteredPlayerBandDiscoveryIntent intent,
            string accessToken,
            string callerAccountId,
            AdaptiveConcurrencyLimiter? limiter,
            CancellationToken ct)
        {
            if (_calls++ == 0)
                throw new EpicLeaderboardUnavailableException();

            return Task.FromResult(
                new RegisteredPlayerBandDiscoveryLookupResult(
                [
                    new BandLeaderboardEntry
                        {
                            TeamKey = "acct1:acct2",
                            TeamMembers = ["acct1", "acct2"],
                            InstrumentCombo = "0:1",
                            Score = 100,
                            MemberStats =
                            [
                                new BandMemberStats
                                {
                                    MemberIndex = 0,
                                    AccountId = "acct1",
                                    InstrumentId = 0,
                                    Score = 50,
                                },
                                new BandMemberStats
                                {
                                    MemberIndex = 1,
                                    AccountId = "acct2",
                                    InstrumentId = 1,
                                    Score = 50,
                                },
                            ],
                        },
                ]));
        }
    }

    private sealed class FailAfterDiscoveryStrategy(int successfulLookups)
            : IRegisteredPlayerBandDiscoveryStrategy
    {
        private int _calls;

        public Task<RegisteredPlayerBandDiscoveryLookupResult> FetchAsync(
            string accountId,
            RegisteredPlayerBandDiscoveryIntent intent,
            string accessToken,
            string callerAccountId,
            AdaptiveConcurrencyLimiter? limiter,
            CancellationToken ct)
        {
            if (_calls++ >= successfulLookups)
                throw new HttpRequestException("Synthetic transient failure.");
            return Task.FromResult(
                RegisteredPlayerBandDiscoveryLookupResult.Empty);
        }
    }

    private sealed class SuccessThenCancelDiscoveryStrategy
        : IRegisteredPlayerBandDiscoveryStrategy
    {
        private int _calls;

        public Task<RegisteredPlayerBandDiscoveryLookupResult> FetchAsync(
            string accountId,
            RegisteredPlayerBandDiscoveryIntent intent,
            string accessToken,
            string callerAccountId,
            AdaptiveConcurrencyLimiter? limiter,
            CancellationToken ct)
        {
            if (_calls++ > 0)
                throw new OperationCanceledException(ct);

            return Task.FromResult(
                new RegisteredPlayerBandDiscoveryLookupResult(
                [
                    new BandLeaderboardEntry
                    {
                        TeamKey = "acct1:acct2",
                        TeamMembers = ["acct1", "acct2"],
                        InstrumentCombo = "0:1",
                        Score = 100,
                        MemberStats =
                        [
                            new BandMemberStats
                            {
                                MemberIndex = 0,
                                AccountId = "acct1",
                                InstrumentId = 0,
                                Score = 50,
                            },
                            new BandMemberStats
                            {
                                MemberIndex = 1,
                                AccountId = "acct2",
                                InstrumentId = 1,
                                Score = 50,
                            },
                        ],
                    },
                ]));
        }
    }
}
