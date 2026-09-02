using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NpgsqlTypes;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class RegisteredBandProcessingOrchestratorTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();

    private MetaDatabase Db => _fixture.Db;

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task RunAsync_persists_entries_and_marks_progress()
    {
        InsertBandProjection("Band_Duets", "acct1:acct2", ["acct1", "acct2"]);
        Db.RegisterSelectedBandActivity("Band_Duets", "acct1:acct2");
        Db.UpsertSeasonWindow(14, "", "");

        var strategy = new FakeRegisteredBandLookupStrategy(new BandLeaderboardEntry
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
        var orchestrator = CreateOrchestrator(strategy, maxLookupsPerBand: 1);
        using var pool = new SharedDopPool(1, 1, 1, 100, Substitute.For<ILogger>());

        var result = await orchestrator.RunAsync(["song-a"], Db.GetSeasonWindows(), "token", "caller", pool);

        Assert.Equal(1, result.BandsProcessed);
        Assert.Equal(1, result.LookupsChecked);
        Assert.Equal(1, result.EntriesFound);
        Assert.Equal(1, result.EntriesPersisted);
        Assert.Contains("acct1:acct2", result.ImpactedTeamsByBandType["Band_Duets"]);

        var status = Db.GetRegisteredBandProcessingStatus("web-band-tracker", "Band_Duets", "acct1:acct2");
        Assert.Equal("in_progress", status?.Status);
        Assert.Equal(1, status?.LookupsChecked);
        Assert.Equal(2, status?.TotalLookupsToCheck);

        var progress = Db.GetCheckedRegisteredBandLookups("web-band-tracker", "Band_Duets", "acct1:acct2");
        Assert.Single(progress);
        Assert.Equal("alltime", progress[0].Scope);

        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT score FROM band_entries WHERE song_id = 'song-a' AND band_type = 'Band_Duets' AND team_key = 'acct1:acct2'";
        Assert.Equal(123456, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    [Fact]
    public async Task RunAsync_checks_all_discovered_seasons_for_registered_band()
    {
        InsertBandProjection("Band_Quad", "acct1:acct2:acct3:acct4", ["acct1", "acct2", "acct3", "acct4"]);
        Db.RegisterSelectedBandActivity("Band_Quad", "acct1:acct2:acct3:acct4");
        Db.UpsertSeasonWindow(12, "", "");
        Db.UpsertSeasonWindow(14, "", "");
        Db.UpsertSeasonWindow(13, "", "");

        var strategy = new CapturingRegisteredBandLookupStrategy();
        var orchestrator = CreateOrchestrator(strategy, maxLookupsPerBand: 10);
        using var pool = new SharedDopPool(1, 1, 1, 100, Substitute.For<ILogger>());

        var result = await orchestrator.RunAsync(["song-a"], Db.GetSeasonWindows(), "token", "caller", pool);

        Assert.Equal(1, result.BandsProcessed);
        Assert.Equal(4, result.LookupsChecked);
        Assert.Equal(0, result.EntriesFound);

        var status = Db.GetRegisteredBandProcessingStatus("web-band-tracker", "Band_Quad", "acct1:acct2:acct3:acct4");
        Assert.Equal("complete", status?.Status);
        Assert.Equal(4, status?.LookupsChecked);
        Assert.Equal(4, status?.TotalLookupsToCheck);

        Assert.Collection(strategy.Intents,
            intent =>
            {
                Assert.Equal("song-a", intent.SongId);
                Assert.Equal(RegisteredBandLookupScope.AllTime, intent.Scope);
                Assert.Equal(0, intent.Season);
                Assert.Equal("alltime", intent.WindowId);
            },
            intent =>
            {
                Assert.Equal("song-a", intent.SongId);
                Assert.Equal(RegisteredBandLookupScope.Season, intent.Scope);
                Assert.Equal(14, intent.Season);
                Assert.Equal("season014", intent.WindowId);
            },
            intent =>
            {
                Assert.Equal("song-a", intent.SongId);
                Assert.Equal(RegisteredBandLookupScope.Season, intent.Scope);
                Assert.Equal(13, intent.Season);
                Assert.Equal("season013", intent.WindowId);
            },
            intent =>
            {
                Assert.Equal("song-a", intent.SongId);
                Assert.Equal(RegisteredBandLookupScope.Season, intent.Scope);
                Assert.Equal(12, intent.Season);
                Assert.Equal("season012", intent.WindowId);
            });
    }

    [Fact]
    public async Task RunAsync_NoncanonicalWindow_InvalidatesConventionalSeasonProgress()
    {
        const string teamKey = "acct1:acct2";
        const string windowId = "season_14_competitive";
        InsertBandProjection("Band_Duets", teamKey, ["acct1", "acct2"]);
        Db.RegisterSelectedBandActivity("Band_Duets", teamKey);
        Db.MarkRegisteredBandLookupChecked(
            "web-band-tracker",
            "Band_Duets",
            teamKey,
            "song-a",
            "alltime",
            0,
            false);
        Db.MarkRegisteredBandLookupChecked(
            "web-band-tracker",
            "Band_Duets",
            teamKey,
            "song-a",
            "season",
            14,
            false,
            "season014");

        var strategy = new CapturingRegisteredBandLookupStrategy();
        var orchestrator = CreateOrchestrator(strategy, maxLookupsPerBand: 10);
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
        var intent = Assert.Single(strategy.Intents);
        Assert.Equal(RegisteredBandLookupScope.Season, intent.Scope);
        Assert.Equal(windowId, intent.WindowId);

        var progress = Db.GetCheckedRegisteredBandLookups(
            "web-band-tracker",
            "Band_Duets",
            teamKey);
        Assert.Equal(2, progress.Count);
        Assert.Equal(
            windowId,
            Assert.Single(progress, row => row.Scope == "season").WindowId);
        var status = Db.GetRegisteredBandProcessingStatus(
            "web-band-tracker",
            "Band_Duets",
            teamKey);
        Assert.Equal("complete", status?.Status);
        Assert.Equal(2, status?.LookupsChecked);
    }

    [Fact]
    public async Task DirectLookupStrategy_sends_exact_noncanonical_window_id()
    {
        const string windowId = "season_14_competitive";
        var scraper = Substitute.For<ILeaderboardQuerier>();
        scraper.LookupBandAsync(
            "song-a",
            "Band_Duets",
            Arg.Any<IReadOnlyList<string>>(),
            windowId,
            "token",
            "caller",
            Arg.Any<AdaptiveConcurrencyLimiter?>(),
            Arg.Any<CancellationToken>())
            .Returns((BandLeaderboardEntry?)null);
        var strategy = new DirectRegisteredBandLookupStrategy(scraper);
        var band = new BandWorkItem
        {
            BandId = "band-test",
            BandType = "Band_Duets",
            TeamKey = "acct1:acct2",
            MemberAccountIds = ["acct1", "acct2"],
        };
        var intent = new RegisteredBandLookupIntent(
            "song-a",
            RegisteredBandLookupScope.Season,
            14,
            windowId);

        await strategy.FetchAsync(
            band,
            intent,
            "token",
            "caller",
            limiter: null,
            CancellationToken.None);

        await scraper.Received(1).LookupBandAsync(
            "song-a",
            "Band_Duets",
            Arg.Is<IReadOnlyList<string>>(members =>
                members.SequenceEqual(new[] { "acct1", "acct2" })),
            windowId,
            "token",
            "caller",
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ResumesWithLeastRecentlyProcessedBandWithinPassBudget()
    {
        const string firstTeam = "acct1:acct2";
        const string secondTeam = "acct3:acct4";
        InsertBandProjection("Band_Duets", firstTeam, ["acct1", "acct2"]);
        InsertBandProjection("Band_Duets", secondTeam, ["acct3", "acct4"]);
        Db.RegisterSelectedBandActivity("Band_Duets", firstTeam);
        Db.RegisterSelectedBandActivity("Band_Duets", secondTeam);

        var strategy = new CapturingRegisteredBandLookupStrategy();
        var orchestrator = CreateOrchestrator(
            strategy,
            maxLookupsPerBand: 2,
            maxLookupsPerPass: 2);
        using var pool = new SharedDopPool(1, 1, 1, 100, Substitute.For<ILogger>());

        var first = await orchestrator.RunAsync(["song-a", "song-b"], [], "token", "caller", pool);
        var second = await orchestrator.RunAsync(["song-a", "song-b"], [], "token", "caller", pool);

        Assert.Equal(2, first.LookupsChecked);
        Assert.Equal(2, second.LookupsChecked);
        Assert.Equal(
            [firstTeam, firstTeam, secondTeam, secondTeam],
            strategy.Calls.Select(call => call.TeamKey));
        Assert.Equal(2, Db.GetCheckedRegisteredBandLookups("web-band-tracker", "Band_Duets", firstTeam).Count);
        Assert.Equal(2, Db.GetCheckedRegisteredBandLookups("web-band-tracker", "Band_Duets", secondTeam).Count);
    }

    [Fact]
    public async Task RunAsync_FailedFirstLookupsConsumeBandPassBudget()
    {
        for (var index = 0; index < 15; index++)
        {
            var teamKey =
                $"acct{index * 2}:acct{index * 2 + 1}";
            InsertBandProjection(
                "Band_Duets",
                teamKey,
                teamKey.Split(':'));
            Db.RegisterSelectedBandActivity(
                "Band_Duets",
                teamKey);
        }

        var strategy =
            new FailingRegisteredBandLookupStrategy();
        var orchestrator = CreateOrchestrator(
            strategy,
            maxLookupsPerBand: 1);
        using var pool = new SharedDopPool(
            1,
            1,
            1,
            100,
            Substitute.For<ILogger>());

        var result = await orchestrator.RunAsync(
            ["song-a"],
            [],
            "token",
            "caller",
            pool);

        Assert.Equal(10, strategy.Calls);
        Assert.Equal(0, result.BandsProcessed);
        Assert.Equal(0, result.LookupsChecked);
        using var conn =
            _fixture.DataSource.OpenConnection();
        using var command = conn.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT(*) FILTER (WHERE status = 'error'),
                COUNT(*)
            FROM registered_band_processing_status
            """;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(10, reader.GetInt64(0));
        Assert.Equal(15, reader.GetInt64(1));
        reader.Close();

        var second = await orchestrator.RunAsync(
            ["song-a"],
            [],
            "token",
            "caller",
            pool);

        Assert.Equal(20, strategy.Calls);
        Assert.Equal(0, second.BandsProcessed);
        command.CommandText = """
            SELECT
                COUNT(*) FILTER (WHERE status = 'error'),
                COUNT(*)
            FROM registered_band_processing_status
            """;
        using var repeatedReader =
            command.ExecuteReader();
        Assert.True(repeatedReader.Read());
        Assert.Equal(15, repeatedReader.GetInt64(0));
        Assert.Equal(15, repeatedReader.GetInt64(1));
    }

    [Fact]
    public void GetRegisteredBands_PrioritizesPendingOverOlderError()
    {
        const string errorTeam =
            "acct-error-1:acct-error-2";
        const string pendingTeam =
            "acct-pending-1:acct-pending-2";
        InsertBandProjection(
            "Band_Duets",
            errorTeam,
            errorTeam.Split(':'));
        InsertBandProjection(
            "Band_Duets",
            pendingTeam,
            pendingTeam.Split(':'));
        Db.RegisterSelectedBandActivity(
            "Band_Duets",
            errorTeam);
        Db.RegisterSelectedBandActivity(
            "Band_Duets",
            pendingTeam);

        using var connection =
            _fixture.DataSource.OpenConnection();
        using var command =
            connection.CreateCommand();
        command.CommandText = """
            UPDATE registered_band_processing_status
            SET status = CASE
                    WHEN team_key = @errorTeam
                        THEN 'error'
                    ELSE 'pending'
                END,
                last_resumed_at = CASE
                    WHEN team_key = @errorTeam
                        THEN now() - interval '1 hour'
                    ELSE now() + interval '1 hour'
                END
            WHERE band_type = 'Band_Duets'
              AND team_key IN (
                    @errorTeam,
                    @pendingTeam)
            """;
        command.Parameters.AddWithValue(
            "errorTeam",
            errorTeam);
        command.Parameters.AddWithValue(
            "pendingTeam",
            pendingTeam);
        Assert.Equal(
            2,
            command.ExecuteNonQuery());

        var bands = Db.GetRegisteredBands();

        Assert.Equal(
            pendingTeam,
            bands[0].TeamKey);
        Assert.Equal(
            errorTeam,
            bands[1].TeamKey);
    }

    private RegisteredBandProcessingOrchestrator CreateOrchestrator(
        IRegisteredBandLookupStrategy strategy,
        int maxLookupsPerBand,
        int maxLookupsPerPass = 80)
    {
        var bandPersistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>());
        var options = Options.Create(new ScraperOptions
        {
            EnableRegisteredBandTargetedProcessing = true,
            RegisteredBandProcessingMaxBandsPerPass = 10,
            RegisteredBandProcessingMaxLookupsPerBand = maxLookupsPerBand,
            RegisteredBandProcessingMaxLookupsPerPass = maxLookupsPerPass,
        });

        return new RegisteredBandProcessingOrchestrator(
            Db,
            bandPersistence,
            strategy,
            new ScrapeProgressTracker(),
            options,
            Substitute.For<ILogger<RegisteredBandProcessingOrchestrator>>(),
            new RegistrationMutationCoordinator(
                Db,
                Substitute.For<IPathDataStore>(),
                Substitute.For<
                    ISongInstrumentSupportCache>()));
    }

    private void InsertBandProjection(string bandType, string teamKey, string[] memberAccountIds)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO band_search_team_projection (band_type, team_key, band_id, appearance_count, member_account_ids, updated_at)
            VALUES (@bandType, @teamKey, @bandId, @appearanceCount, @memberAccountIds, @updatedAt)
            """;
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        cmd.Parameters.AddWithValue("bandId", $"test-{bandType}-{teamKey}");
        cmd.Parameters.AddWithValue("appearanceCount", 1);
        cmd.Parameters.Add("memberAccountIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = memberAccountIds;
        cmd.Parameters.AddWithValue("updatedAt", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    private sealed class FakeRegisteredBandLookupStrategy : IRegisteredBandLookupStrategy
    {
        private readonly BandLeaderboardEntry _entry;

        public FakeRegisteredBandLookupStrategy(BandLeaderboardEntry entry)
        {
            _entry = entry;
        }

        public Task<RegisteredBandLookupResult> FetchAsync(
            BandWorkItem band,
            RegisteredBandLookupIntent intent,
            string accessToken,
            string callerAccountId,
            AdaptiveConcurrencyLimiter? limiter,
            CancellationToken ct)
        {
            if (intent.Scope == RegisteredBandLookupScope.AllTime)
                return Task.FromResult(new RegisteredBandLookupResult([_entry]));

            return Task.FromResult(RegisteredBandLookupResult.Empty);
        }
    }

    private sealed class CapturingRegisteredBandLookupStrategy : IRegisteredBandLookupStrategy
    {
        public List<RegisteredBandLookupIntent> Intents { get; } = [];
        public List<(string TeamKey, RegisteredBandLookupIntent Intent)> Calls { get; } = [];

        public Task<RegisteredBandLookupResult> FetchAsync(
            BandWorkItem band,
            RegisteredBandLookupIntent intent,
            string accessToken,
            string callerAccountId,
            AdaptiveConcurrencyLimiter? limiter,
            CancellationToken ct)
        {
            Intents.Add(intent);
            Calls.Add((band.TeamKey, intent));
            return Task.FromResult(RegisteredBandLookupResult.Empty);
        }
    }

    private sealed class FailingRegisteredBandLookupStrategy
        : IRegisteredBandLookupStrategy
    {
        public int Calls { get; private set; }

        public Task<RegisteredBandLookupResult> FetchAsync(
            BandWorkItem band,
            RegisteredBandLookupIntent intent,
            string accessToken,
            string callerAccountId,
            AdaptiveConcurrencyLimiter? limiter,
            CancellationToken ct)
        {
            Calls++;
            throw new HttpRequestException(
                "Synthetic invalid leaderboard.");
        }
    }
}