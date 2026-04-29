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

    private RegisteredBandProcessingOrchestrator CreateOrchestrator(IRegisteredBandLookupStrategy strategy, int maxLookupsPerBand)
    {
        var bandPersistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>());
        var options = Options.Create(new ScraperOptions
        {
            EnableRegisteredBandTargetedProcessing = true,
            RegisteredBandProcessingMaxBandsPerPass = 10,
            RegisteredBandProcessingMaxLookupsPerBand = maxLookupsPerBand,
        });

        return new RegisteredBandProcessingOrchestrator(
            Db,
            bandPersistence,
            strategy,
            new ScrapeProgressTracker(),
            options,
            Substitute.For<ILogger<RegisteredBandProcessingOrchestrator>>());
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
}