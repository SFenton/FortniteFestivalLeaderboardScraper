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

    [Fact]
    public void RecomputeOverThresholdFlags_UsesMultiplierAndKnownChoptInstruments()
    {
        SeedSongWithMaxScores("song_threshold", maxVocalsScore: 100_000);
        var persistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<BandLeaderboardPersistence>>());

        persistence.UpsertBandEntries("song_threshold", "Band_Duets",
        [
            MakeBandEntry(
                ["under-a", "under-b"],
                "2:7",
                100_000,
                isOverThreshold: true,
                memberStats:
                [
                    MakeMember(0, "under-a", instrumentId: 2, score: 100_000),
                    MakeMember(1, "under-b", instrumentId: 7, score: 500_000),
                ]),
            MakeBandEntry(
                ["over-a", "over-b"],
                "2:7",
                106_000,
                isOverThreshold: false,
                memberStats:
                [
                    MakeMember(0, "over-a", instrumentId: 2, score: 106_000),
                    MakeMember(1, "over-b", instrumentId: 7, score: 500_000),
                ]),
        ]);

        var changed = _sut.RecomputeOverThresholdFlags(["Band_Duets"], overThresholdMultiplier: 1.05);

        Assert.Equal(2, changed);
        Assert.False(GetIsOverThreshold("song_threshold", "Band_Duets", "under-a:under-b", "2:7"));
        Assert.True(GetIsOverThreshold("song_threshold", "Band_Duets", "over-a:over-b", "2:7"));
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

    private void SeedSongWithMaxScores(string songId, int? maxVocalsScore = null)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO songs (song_id, title, artist, max_vocals_score)
            VALUES (@songId, @title, @artist, @maxVocalsScore)
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("title", songId);
        cmd.Parameters.AddWithValue("artist", "artist");
        cmd.Parameters.AddWithValue("maxVocalsScore", maxVocalsScore.HasValue ? maxVocalsScore.Value : DBNull.Value);
        cmd.ExecuteNonQuery();
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

    private bool GetIsOverThreshold(string songId, string bandType, string teamKey, string instrumentCombo)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT is_over_threshold
            FROM band_entries
            WHERE song_id = @songId
              AND band_type = @bandType
              AND team_key = @teamKey
              AND instrument_combo = @instrumentCombo
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        cmd.Parameters.AddWithValue("instrumentCombo", instrumentCombo);
        return (bool)cmd.ExecuteScalar()!;
    }

    private static BandLeaderboardEntry MakeBandEntry(
        string[] teamMembers,
        string instrumentCombo,
        int score,
        bool isFullCombo = false,
        bool isOverThreshold = false,
        List<BandMemberStats>? memberStats = null)
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
            IsOverThreshold = isOverThreshold,
            MemberStats = memberStats ?? [],
        };
    }

    private static BandMemberStats MakeMember(int memberIndex, string accountId, int instrumentId, int score) => new()
    {
        MemberIndex = memberIndex,
        AccountId = accountId,
        InstrumentId = instrumentId,
        Score = score,
        Accuracy = 950000,
        Stars = 5,
        Difficulty = 3,
    };
}