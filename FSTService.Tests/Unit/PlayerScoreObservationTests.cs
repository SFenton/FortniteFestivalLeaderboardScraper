using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class PlayerScoreObservationTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new(new FeatureOptions
    {
        WriteSoloScoreObservations = true,
        WriteBandMemberScoreObservations = true,
    });
    private MetaDatabase Db => _fixture.Db;

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void InsertScoreChange_WritesSoloObservationAndUnionRow()
    {
        Db.InsertScoreChange(
            "song-1", "Solo_Guitar", "acct-1",
            oldScore: null, newScore: 123_456, oldRank: null, newRank: 42,
            accuracy: 987_654, isFullCombo: true, stars: 5, percentile: 0.12,
            season: 11, scoreAchievedAt: "2025-11-13T04:35:38.651Z",
            seasonRank: 7, allTimeRank: 42, difficulty: 3);

        using var connection = _fixture.DataSource.OpenConnection();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT source_kind, score, solo_rank, season_rank, all_time_rank, band_rank, source_scope
                FROM player_score_observations
                WHERE account_id = 'acct-1' AND song_id = 'song-1' AND instrument = 'Solo_Guitar'
                """;
            using var reader = command.ExecuteReader();

            Assert.True(reader.Read());
            Assert.Equal("solo-history", reader.GetString(0));
            Assert.Equal(123_456, reader.GetInt32(1));
            Assert.Equal(42, reader.GetInt32(2));
            Assert.Equal(7, reader.GetInt32(3));
            Assert.Equal(42, reader.GetInt32(4));
            Assert.True(reader.IsDBNull(5));
            Assert.Equal("season:11", reader.GetString(6));
            Assert.False(reader.Read());
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT score, source_count, source_kinds
                FROM player_score_observation_union
                WHERE account_id = 'acct-1' AND song_id = 'song-1' AND instrument = 'Solo_Guitar'
                """;
            using var reader = command.ExecuteReader();

            Assert.True(reader.Read());
            Assert.Equal(123_456, reader.GetInt32(0));
            Assert.Equal(1, reader.GetInt32(1));
            Assert.Equal(["solo-history"], reader.GetFieldValue<string[]>(2));
            Assert.False(reader.Read());
        }
    }

    [Fact]
    public void InsertScoreChanges_DedupesSoloObservationBySourceIdentity()
    {
        var change = new ScoreChangeRecord
        {
            SongId = "song-1",
            Instrument = "Solo_Guitar",
            AccountId = "acct-1",
            NewScore = 123_456,
            NewRank = 42,
            Accuracy = 987_654,
            IsFullCombo = true,
            Stars = 5,
            Percentile = 0.12,
            Season = 11,
            ScoreAchievedAt = "2025-11-13T04:35:38.651Z",
            SeasonRank = 7,
            AllTimeRank = 42,
            Difficulty = 3,
        };

        Db.InsertScoreChanges([change]);
        Db.InsertScoreChanges([change]);

        Assert.Equal(1, ScalarInt("""
            SELECT COUNT(*)
            FROM player_score_observations
            WHERE account_id = 'acct-1' AND song_id = 'song-1' AND instrument = 'Solo_Guitar'
            """));
    }

    [Fact]
    public void UpsertBandEntries_WritesBandMemberObservationWithoutSoloRank()
    {
        var persistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>(),
            Options.Create(new FeatureOptions { WriteBandMemberScoreObservations = true }));

        persistence.UpsertBandEntries("song-1", "Band_Duets", [new BandLeaderboardEntry
        {
            TeamKey = "acct-1:acct-2",
            TeamMembers = ["acct-1", "acct-2"],
            InstrumentCombo = "0:1",
            Score = 222_222,
            Accuracy = 900_000,
            IsFullCombo = false,
            Stars = 5,
            Difficulty = 3,
            Season = 11,
            Rank = 99,
            Percentile = 0.34,
            EndTime = "2025-11-13T04:35:38.651Z",
            Source = "findteams",
            MemberStats =
            [
                new BandMemberStats
                {
                    MemberIndex = 0,
                    AccountId = "acct-1",
                    InstrumentId = 0,
                    Score = 111_111,
                    Accuracy = 987_654,
                    IsFullCombo = true,
                    Stars = 5,
                    Difficulty = 3,
                },
                new BandMemberStats
                {
                    MemberIndex = 1,
                    AccountId = "acct-2",
                    InstrumentId = 1,
                    Score = 101_111,
                    Accuracy = 876_543,
                    IsFullCombo = false,
                    Stars = 5,
                    Difficulty = 3,
                },
            ],
        }]);

        using var connection = _fixture.DataSource.OpenConnection();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT source_kind, score, solo_rank, season_rank, all_time_rank,
                       band_type, team_key, band_score, band_rank, band_source, source_scope
                FROM player_score_observations
                WHERE account_id = 'acct-1' AND song_id = 'song-1' AND instrument = 'Solo_Guitar'
                """;
            using var reader = command.ExecuteReader();

            Assert.True(reader.Read());
            Assert.Equal("band-member", reader.GetString(0));
            Assert.Equal(111_111, reader.GetInt32(1));
            Assert.True(reader.IsDBNull(2));
            Assert.True(reader.IsDBNull(3));
            Assert.True(reader.IsDBNull(4));
            Assert.Equal("Band_Duets", reader.GetString(5));
            Assert.Equal("acct-1:acct-2", reader.GetString(6));
            Assert.Equal(222_222, reader.GetInt32(7));
            Assert.Equal(99, reader.GetInt32(8));
            Assert.Equal("findteams", reader.GetString(9));
            Assert.Equal("season:11", reader.GetString(10));
            Assert.False(reader.Read());
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT score, source_kinds
                FROM player_score_observation_union
                WHERE account_id = 'acct-1' AND song_id = 'song-1' AND instrument = 'Solo_Guitar'
                """;
            using var reader = command.ExecuteReader();

            Assert.True(reader.Read());
            Assert.Equal(111_111, reader.GetInt32(0));
            Assert.Equal(["band-member"], reader.GetFieldValue<string[]>(1));
            Assert.False(reader.Read());
        }
    }

    [Fact]
    public void DefaultFlags_KeepScoreHistoryButSkipSoloObservations()
    {
        using var fixture = new InMemoryMetaDatabase();
        var changes = Enumerable.Range(0, 21)
            .Select(index => new ScoreChangeRecord
            {
                SongId = "song-1",
                Instrument = "Solo_Guitar",
                AccountId = $"acct-{index}",
                NewScore = 100_000 + index,
                NewRank = index + 1,
                Season = 11,
            })
            .ToArray();

        fixture.Db.InsertScoreChange(
            "song-single", "Solo_Guitar", "acct-single",
            oldScore: null, newScore: 123_456, oldRank: null, newRank: 1);
        fixture.Db.InsertScoreChanges(changes);

        using var connection = fixture.DataSource.OpenConnection();
        Assert.Equal(22, ScalarInt(connection, "SELECT COUNT(*) FROM score_history"));
        Assert.Equal(0, ScalarInt(connection, "SELECT COUNT(*) FROM player_score_observations"));
    }

    [Fact]
    public void DefaultFlags_KeepBandFactsButSkipBandMemberObservations()
    {
        using var fixture = new InMemoryMetaDatabase();
        var persistence = new BandLeaderboardPersistence(
            fixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>());

        persistence.UpsertBandEntries("song-1", "Band_Duets", [new BandLeaderboardEntry
        {
            TeamKey = "acct-1:acct-2",
            TeamMembers = ["acct-1", "acct-2"],
            InstrumentCombo = "0:1",
            Score = 222_222,
            Accuracy = 900_000,
            IsFullCombo = false,
            Stars = 5,
            Difficulty = 3,
            Season = 11,
            Rank = 99,
            Percentile = 0.34,
            Source = "findteams",
            MemberStats =
            [
                new BandMemberStats { MemberIndex = 0, AccountId = "acct-1", InstrumentId = 0, Score = 111_111 },
                new BandMemberStats { MemberIndex = 1, AccountId = "acct-2", InstrumentId = 1, Score = 101_111 },
            ],
        }]);

        using var connection = fixture.DataSource.OpenConnection();
        Assert.Equal(1, ScalarInt(connection, "SELECT COUNT(*) FROM band_entries"));
        Assert.Equal(2, ScalarInt(connection, "SELECT COUNT(*) FROM band_member_stats"));
        Assert.Equal(0, ScalarInt(connection, "SELECT COUNT(*) FROM player_score_observations"));
    }

    private int ScalarInt(string sql)
    {
        using var connection = _fixture.DataSource.OpenConnection();
        return ScalarInt(connection, sql);
    }

    private static int ScalarInt(Npgsql.NpgsqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
