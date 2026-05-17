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
            _metaFixture.DataSource,
            Options.Create(new FeatureOptions()));
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

    private static void SeedCurrentProjection(IInstrumentDatabase db,
        params (string SongId, (string AccountId, int Score, int Rank)[] Entries)[] songs)
    {
        var pgDb = (InstrumentDatabase)db;
        var builder = new SoloCurrentProjectionBuilder(pgDb.DataSource, NullLogger<SoloCurrentProjectionBuilder>.Instance);
        builder.EnsureSchemaAsync().GetAwaiter().GetResult();

        using var conn = pgDb.DataSource.OpenConnection();
        foreach (var (songId, entries) in songs)
        {
            using (var scopeCmd = conn.CreateCommand())
            {
                scopeCmd.CommandText = """
                    INSERT INTO solo_current_projection_scope
                    (song_id, instrument, projection_generation, row_count, source_snapshot_id, status, error_message, last_rebuilt_at, updated_at)
                    VALUES (@songId, @instrument, 1, @rowCount, NULL, 'ready', NULL, @now, @now)
                    ON CONFLICT (song_id, instrument) DO UPDATE SET
                        projection_generation = EXCLUDED.projection_generation,
                        row_count = EXCLUDED.row_count,
                        source_snapshot_id = EXCLUDED.source_snapshot_id,
                        status = EXCLUDED.status,
                        error_message = EXCLUDED.error_message,
                        last_rebuilt_at = EXCLUDED.last_rebuilt_at,
                        updated_at = EXCLUDED.updated_at
                    """;
                scopeCmd.Parameters.AddWithValue("songId", songId);
                scopeCmd.Parameters.AddWithValue("instrument", db.Instrument);
                scopeCmd.Parameters.AddWithValue("rowCount", entries.Length);
                scopeCmd.Parameters.AddWithValue("now", DateTime.UtcNow);
                scopeCmd.ExecuteNonQuery();
            }

            foreach (var (accountId, score, rank) in entries)
            {
                using var entryCmd = conn.CreateCommand();
                entryCmd.CommandText = """
                    INSERT INTO current_leaderboard_entries
                    (song_id, instrument, account_id, score, accuracy, is_full_combo, stars, season, percentile, rank,
                     api_rank, source, difficulty, end_time, first_seen_at, last_updated_at, projection_generation, computed_at)
                    VALUES
                    (@songId, @instrument, @accountId, @score, 95, false, 5, 3, 99.0, @rank,
                     @rank, 'projection-test', 3, '2025-01-15T12:00:00Z', @now, @now, 1, @now)
                    ON CONFLICT (song_id, instrument, account_id) DO UPDATE SET
                        score = EXCLUDED.score,
                        rank = EXCLUDED.rank,
                        api_rank = EXCLUDED.api_rank,
                        source = EXCLUDED.source,
                        last_updated_at = EXCLUDED.last_updated_at
                    """;
                entryCmd.Parameters.AddWithValue("songId", songId);
                entryCmd.Parameters.AddWithValue("instrument", db.Instrument);
                entryCmd.Parameters.AddWithValue("accountId", accountId);
                entryCmd.Parameters.AddWithValue("score", score);
                entryCmd.Parameters.AddWithValue("rank", rank);
                entryCmd.Parameters.AddWithValue("now", DateTime.UtcNow);
                entryCmd.ExecuteNonQuery();
            }
        }
    }

    [Fact]
    public void ComputeForUser_ReturnsEmptyWhenUserHasNoScores()
    {
        var result = _sut.ComputeForUser("nonexistent");
        Assert.Equal(0, result.RivalCount);
        Assert.Equal(0, result.SampleCount);
    }

    [Fact]
    public void ComputeInstrument_ComputesLiveWithoutPersistingRows()
    {
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        SeedScoresAndRankings(db,
            ("song1", [("user", 1000), ("above1", 1100), ("below1", 900)]),
            ("song2", [("user", 2000), ("above1", 2100), ("below1", 1900)]));

        var result = _sut.ComputeInstrument("user", "Solo_Guitar", "totalscore");

        Assert.True(result.UserFound);
        Assert.NotEmpty(result.Rivals);
        Assert.NotEmpty(result.Samples);
        Assert.Empty(_metaFixture.Db.GetLeaderboardRivals("user", "Solo_Guitar", "totalscore"));
    }

    [Fact]
    public void ComputeInstrument_UsesCurrentStateScoresForNeighborSamples()
    {
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        SeedScoresAndRankings(db,
            ("song1", [("user", 1000), ("neighbor", 1100)]),
            ("song2", [("user", 2000), ("neighbor", 2100)]));
        SeedCurrentProjection(db,
            ("song1", [("neighbor", 9100, 1), ("user", 1000, 2)]),
            ("song2", [("neighbor", 9200, 1), ("user", 2000, 2)]));

        var result = _sut.ComputeInstrument("user", "Solo_Guitar", "totalscore");
        var samples = result.Samples.Where(s => s.RivalAccountId == "neighbor").ToList();

        Assert.True(result.UserFound);
        Assert.NotEmpty(samples);
        Assert.Contains(samples, sample => sample.SongId == "song1" && sample.RivalScore == 9100);
        Assert.Contains(samples, sample => sample.SongId == "song2" && sample.RivalScore == 9200);
        Assert.DoesNotContain(samples, sample => sample.RivalScore is 1100 or 2100);
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
