using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class BandRankingRepairServiceTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();
    private readonly BandRankingRepairService _sut;

    public BandRankingRepairServiceTests()
    {
        _sut = new BandRankingRepairService(
            _fixture.Db,
            _fixture.DataSource,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<BandRankingRepairService>>());
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Inspect_ReportsSourceCounts()
    {
        SeedSongs("song_0", "song_1");
        SeedBandEntries();

        var overview = _sut.Inspect(["Band_Duets"]);

        Assert.Equal(2, overview.TotalChartedSongs);
        Assert.Single(overview.Bands);
        Assert.Equal(6, overview.Bands[0].SourceRows);
        Assert.Equal(6, overview.Bands[0].RankableRows);
        Assert.Equal(0, overview.Bands[0].RankingRows);
        Assert.Equal(0, overview.Bands[0].OverallTeams);
    }

    [Fact]
    public void Rebuild_PopulatesBandRankingTables_UsesDefaultMonolithicWriteModeWithoutAnalyze()
    {
        SeedSongs("song_0", "song_1");
        SeedBandEntries();

        var results = _sut.Rebuild(["Band_Duets"]);

        var result = Assert.Single(results);
        Assert.Equal(2, result.TotalChartedSongs);
        Assert.Equal(0, result.Before.RankingRows);
        Assert.True(result.After.RankingRows > 0);
        Assert.Equal(3, result.After.OverallTeams);
        Assert.True(result.After.ComboCatalogEntries > 0);
        Assert.NotNull(result.Metrics);
        Assert.Equal(BandTeamRankingWriteMode.Monolithic, result.Metrics!.WriteMode);
        Assert.Equal(0, result.Metrics.AnalyzeResultsMs);
        Assert.True(result.Metrics.ResultRowCount > 0);
    }

    private void SeedSongs(params string[] songIds)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        foreach (var songId in songIds)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO songs (song_id, title, artist) VALUES (@songId, @title, @artist)";
            cmd.Parameters.AddWithValue("songId", songId);
            cmd.Parameters.AddWithValue("title", songId);
            cmd.Parameters.AddWithValue("artist", "artist");
            cmd.ExecuteNonQuery();
        }
    }

    private void SeedBandEntries()
    {
        var persistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<BandLeaderboardPersistence>>());

        persistence.UpsertBandEntries("song_0", "Band_Duets",
        [
            MakeBandEntry(["p1", "p2"], "0:1", 1000, isFullCombo: true),
            MakeBandEntry(["p3", "p4"], "0:3", 900),
            MakeBandEntry(["p1", "p2"], "0:0", 1100),
        ]);
        persistence.UpsertBandEntries("song_1", "Band_Duets",
        [
            MakeBandEntry(["p1", "p2"], "0:1", 1200),
            MakeBandEntry(["p3", "p4"], "0:3", 1300, isFullCombo: true),
            MakeBandEntry(["p5", "p6"], "0:1", 800),
        ]);
    }

    private static BandLeaderboardEntry MakeBandEntry(string[] teamMembers, string instrumentCombo, int score, bool isFullCombo = false)
    {
        var sortedMembers = teamMembers.OrderBy(static member => member, StringComparer.OrdinalIgnoreCase).ToArray();
        return new BandLeaderboardEntry
        {
            TeamKey = string.Join(':', sortedMembers),
            TeamMembers = teamMembers,
            InstrumentCombo = instrumentCombo,
            Score = score,
            Accuracy = 950000,
            IsFullCombo = isFullCombo,
            Stars = 5,
            Difficulty = 3,
            Season = 1,
            Rank = 1,
            Percentile = 0.5,
            Source = "scrape",
        };
    }
}