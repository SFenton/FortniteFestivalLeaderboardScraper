using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class ComboRankingRepairServiceTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly ComboRankingRepairService _sut;

    public ComboRankingRepairServiceTests()
    {
        _persistence = new GlobalLeaderboardPersistence(
            _fixture.Db,
            _loggerFactory,
            Substitute.For<ILogger<GlobalLeaderboardPersistence>>(),
            _fixture.DataSource,
            Options.Create(new FeatureOptions()));
        _persistence.Initialize();

        _sut = new ComboRankingRepairService(
            _persistence,
            _fixture.Db,
            _fixture.DataSource,
            Substitute.For<ILogger<ComboRankingRepairService>>());
    }

    public void Dispose()
    {
        _persistence.Dispose();
        _fixture.Dispose();
    }

    [Fact]
    public void Inspect_ReportsExpectedIntersectionCounts()
    {
        SeedComboSourceData();

        var overview = _sut.Inspect(["03"]);

        var combo = Assert.Single(overview.Combos);
        Assert.Equal("03", combo.ComboId);
        Assert.Equal(2, combo.ExpectedAccounts);
        Assert.Equal(0, combo.LeaderboardRows);
        Assert.Equal(0, combo.StatsTotalAccounts);
    }

    [Fact]
    public void Rebuild_PopulatesComboLeaderboardFromPersistedRankings()
    {
        SeedComboSourceData();

        var results = _sut.Rebuild(["03"]);

        var result = Assert.Single(results);
        Assert.Equal(2, result.After.ExpectedAccounts);
        Assert.Equal(2, result.After.LeaderboardRows);
        Assert.Equal(2, result.After.StatsTotalAccounts);

        var (entries, totalAccounts) = _fixture.Db.GetComboLeaderboard("03", pageSize: 10);
        Assert.Equal(2, totalAccounts);
        Assert.Equal(2, entries.Count);
        Assert.Equal("p1", entries[0].AccountId);
    }

    [Fact]
    public void Rebuild_ClearsStaleRowsWhenNoAccountsStillQualify()
    {
        _fixture.Db.ReplaceComboLeaderboard("03",
        [
            ("stale", 0.75, 0.8, 0.5, 1234L, 0.9, 2, 1),
        ],
        totalAccounts: 1);

        SeedSingleInstrumentOnly();

        var results = _sut.Rebuild(["03"]);

        var result = Assert.Single(results);
        Assert.Equal(0, result.After.ExpectedAccounts);
        Assert.Equal(0, result.After.LeaderboardRows);
        Assert.Equal(0, result.After.StatsTotalAccounts);

        var (entries, totalAccounts) = _fixture.Db.GetComboLeaderboard("03", pageSize: 10);
        Assert.Empty(entries);
        Assert.Equal(0, totalAccounts);
    }

    private void SeedComboSourceData()
    {
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        var bassDb = _persistence.GetOrCreateInstrumentDb("Solo_Bass");

        guitarDb.UpsertEntries("song_0",
        [
            MakeEntry("p1", 1000, rank: 1),
            MakeEntry("p2", 950, rank: 2),
            MakeEntry("guitar_only", 900, rank: 3),
        ]);
        bassDb.UpsertEntries("song_0",
        [
            MakeEntry("p1", 990, rank: 1),
            MakeEntry("p2", 900, rank: 2),
            MakeEntry("bass_only", 850, rank: 3),
        ]);

        guitarDb.RecomputeAllRanks();
        bassDb.RecomputeAllRanks();
        guitarDb.ComputeSongStats();
        bassDb.ComputeSongStats();
        guitarDb.ComputeAccountRankings(totalChartedSongs: 1);
        bassDb.ComputeAccountRankings(totalChartedSongs: 1);
    }

    private void SeedSingleInstrumentOnly()
    {
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song_0",
        [
            MakeEntry("guitar_only", 1000, rank: 1),
        ]);
        guitarDb.RecomputeAllRanks();
        guitarDb.ComputeSongStats();
        guitarDb.ComputeAccountRankings(totalChartedSongs: 1);
    }

    private static LeaderboardEntry MakeEntry(string accountId, int score, int rank)
        => new()
        {
            AccountId = accountId,
            Score = score,
            Rank = rank,
            Accuracy = 95,
            IsFullCombo = false,
            Stars = 5,
            Season = 1,
        };
}