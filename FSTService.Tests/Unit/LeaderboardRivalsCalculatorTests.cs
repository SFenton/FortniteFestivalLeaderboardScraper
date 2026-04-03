using FortniteFestival.Core.Models;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class LeaderboardRivalsCalculatorTests : IDisposable
{
    private readonly InMemoryMetaDatabase _metaFixture = new();
    private readonly string _dataDir;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly LeaderboardRivalsCalculator _sut;

    public LeaderboardRivalsCalculatorTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"fst_lb_rivals_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);

        _persistence = new GlobalLeaderboardPersistence(
            _metaFixture.Db, new NullLoggerFactory(),
            NullLogger<GlobalLeaderboardPersistence>.Instance,
            _metaFixture.DataSource);
        _persistence.Initialize();

        _sut = new LeaderboardRivalsCalculator(
            _persistence, _metaFixture.Db,
            Options.Create(new ScraperOptions { LeaderboardRivalRadius = 3 }),
            Substitute.For<ILogger<LeaderboardRivalsCalculator>>());
    }

    public void Dispose()
    {
        _metaFixture.Dispose();
        _persistence.Dispose();
        try { Directory.Delete(_dataDir, true); } catch { }
    }

    private static void SeedScoresAndRankings(IInstrumentDatabase db,
        params (string SongId, (string AccountId, int Score)[] Entries)[] songs)
    {
        // 1) Insert leaderboard entries (so GetPlayerScores works)
        foreach (var (songId, entries) in songs)
        {
            db.UpsertEntries(songId, entries.Select(e => new LeaderboardEntry
            {
                AccountId = e.AccountId,
                Score = e.Score,
                Accuracy = 95,
                IsFullCombo = false,
                Stars = 5,
                Season = 3,
                Percentile = 99.0,
                EndTime = "2025-01-15T12:00:00Z",
            }).ToList());
        }

        // 2) RecomputeAllRanks sets per-song Rank values in LeaderboardEntries
        db.RecomputeAllRanks();

        // 3) ComputeSongStats populates entry counts/log weights needed by ranking CTE
        db.ComputeSongStats();

        // 4) ComputeAccountRankings populates the AccountRankings table
        db.ComputeAccountRankings(songs.Length);
    }

    [Fact]
    public void ComputeForUser_ReturnsEmptyWhenUserHasNoScores()
    {
        var result = _sut.ComputeForUser("nonexistent");
        Assert.Equal(0, result.RivalCount);
        Assert.Equal(0, result.SampleCount);
    }

    [Fact]
    public void ComputeForUser_FindsNeighborsAndBuildsRivals()
    {
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        // Seed 5 players across 3 songs
        SeedScoresAndRankings(db,
            ("song1", [("user", 1000), ("above1", 1100), ("above2", 1200), ("below1", 900), ("below2", 800)]),
            ("song2", [("user", 2000), ("above1", 2100), ("below1", 1900)]),
            ("song3", [("user", 3000), ("above2", 3100)]));

        // Verify rankings were populated
        var ranking = db.GetAccountRanking("user");
        Assert.NotNull(ranking);
        var (above, self, below) = db.GetAccountRankingNeighborhood("user", radius: 3, rankBy: "totalscore");
        Assert.NotNull(self);

        var result = _sut.ComputeForUser("user");

        Assert.True(result.RivalCount > 0, "Should find at least one rival");
        Assert.True(result.SampleCount > 0, "Should have song samples");

        // Verify data was persisted
        var rivals = _metaFixture.Db.GetLeaderboardRivals("user", "Solo_Guitar", "totalscore");
        Assert.NotEmpty(rivals);

        var aboveRivals = rivals.Where(r => r.Direction == "above").ToList();
        var belowRivals = rivals.Where(r => r.Direction == "below").ToList();

        // Should have above and/or below rivals
        Assert.True(aboveRivals.Count > 0 || belowRivals.Count > 0);
    }

    [Fact]
    public void ComputeForUser_ComputesAcrossMultipleRankMethods()
    {
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        SeedScoresAndRankings(db,
            ("song1", [("user", 1000), ("neighbor1", 1100), ("neighbor2", 900)]),
            ("song2", [("user", 2000), ("neighbor1", 2100), ("neighbor2", 1900)]));

        var result = _sut.ComputeForUser("user");

        // Should compute for all 5 rank methods
        var rivals = _metaFixture.Db.GetLeaderboardRivals("user", "Solo_Guitar");
        var methods = rivals.Select(r => r.RankMethod).Distinct().ToHashSet();

        // At least totalscore should be present
        Assert.Contains("totalscore", methods);
    }

    [Fact]
    public void ComputeForUser_PersistsSharedSongCountsCorrectly()
    {
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        // user and neighbor share 2 songs
        SeedScoresAndRankings(db,
            ("song1", [("user", 1000), ("neighbor", 1100)]),
            ("song2", [("user", 2000), ("neighbor", 2100)]),
            // neighbor has a 3rd song user doesn't
            ("song3", [("neighbor", 3000)]));

        _sut.ComputeForUser("user");

        var rivals = _metaFixture.Db.GetLeaderboardRivals("user", "Solo_Guitar", "totalscore");
        var neighborRival = rivals.FirstOrDefault(r => r.RivalAccountId == "neighbor");
        Assert.NotNull(neighborRival);
        Assert.Equal(2, neighborRival.SharedSongCount);
    }

    [Fact]
    public void ComputeForUser_PersistsSongSamples()
    {
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        SeedScoresAndRankings(db,
            ("song1", [("user", 1000), ("neighbor", 1100)]),
            ("song2", [("user", 2000), ("neighbor", 1900)]));

        _sut.ComputeForUser("user");

        var samples = _metaFixture.Db.GetLeaderboardRivalSongSamples("user", "neighbor", "Solo_Guitar", "totalscore");
        Assert.NotEmpty(samples);

        // Should have samples for each shared song
        var songIds = samples.Select(s => s.SongId).ToHashSet();
        Assert.Contains("song1", songIds);
        Assert.Contains("song2", songIds);
    }

    [Fact]
    public void ComputeForUser_PreservesExistingRivalsWhenUserNotInAccountRankings()
    {
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        // Seed scores for "user" and a neighbor, compute rankings + rivals normally
        SeedScoresAndRankings(db,
            ("song1", [("user", 1000), ("neighbor", 1100)]),
            ("song2", [("user", 2000), ("neighbor", 2100)]));

        _sut.ComputeForUser("user");
        var initialRivals = _metaFixture.Db.GetLeaderboardRivals("user", "Solo_Guitar", "totalscore");
        Assert.NotEmpty(initialRivals);

        // Now clear AccountRankings (simulating a failed or empty ranking computation)
        var pgDb = (InstrumentDatabase)db;
        using (var conn = pgDb.DataSource.OpenConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM account_rankings WHERE instrument = 'Solo_Guitar';";
            cmd.ExecuteNonQuery();
        }

        // Compute again — user no longer in AccountRankings
        _sut.ComputeForUser("user");

        // Existing rivals should be PRESERVED (not wiped)
        var afterRivals = _metaFixture.Db.GetLeaderboardRivals("user", "Solo_Guitar", "totalscore");
        Assert.NotEmpty(afterRivals);
        Assert.Equal(initialRivals.Count, afterRivals.Count);
    }

    [Fact]
    public void ComputeForUser_ReplacesWithEmptyWhenUserRankedButNoNeighbors()
    {
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        // Seed two users so we can compute rivals initially
        SeedScoresAndRankings(db,
            ("song1", [("user", 1000), ("neighbor", 1100)]),
            ("song2", [("user", 2000), ("neighbor", 2100)]));

        _sut.ComputeForUser("user");
        var initialRivals = _metaFixture.Db.GetLeaderboardRivals("user", "Solo_Guitar", "totalscore");
        Assert.NotEmpty(initialRivals);

        // Remove the neighbor so user is ranked but alone (no neighbors in radius)
        var pgDb = (InstrumentDatabase)db;
        using (var conn = pgDb.DataSource.OpenConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM leaderboard_entries WHERE account_id = 'neighbor' AND instrument = 'Solo_Guitar';";
            cmd.ExecuteNonQuery();
        }

        // Recompute rankings with only the user
        db.RecomputeAllRanks();
        db.ComputeSongStats();
        db.ComputeAccountRankings(2);

        // Compute rivals — user IS ranked but has no neighbors
        _sut.ComputeForUser("user");

        // Rivals should be cleared (legitimate empty state)
        var afterRivals = _metaFixture.Db.GetLeaderboardRivals("user", "Solo_Guitar", "totalscore");
        Assert.Empty(afterRivals);
    }
}
