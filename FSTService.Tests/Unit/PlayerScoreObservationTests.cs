using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class PlayerScoreObservationRetirementTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void ScoreHistoryWritesRemainExactWithoutRetiredObservationSchema()
    {
        AssertRetiredObservationSchemaAbsent();

        _fixture.Db.InsertScoreChange(
            "song-single", "Solo_Guitar", "acct-single",
            oldScore: 100_000, newScore: 123_456, oldRank: 2, newRank: 1,
            accuracy: 987_654, isFullCombo: true, stars: 6, percentile: 0.01,
            season: 11, scoreAchievedAt: "2025-11-13T04:35:38.651Z",
            seasonRank: 7, allTimeRank: 42, difficulty: 3);

        var changes = Enumerable.Range(0, 21)
            .Select(index => new ScoreChangeRecord
            {
                SongId = $"song-{index}",
                Instrument = "Solo_Guitar",
                AccountId = $"acct-{index}",
                NewScore = 100_000 + index,
                NewRank = index + 1,
                Season = 11,
            })
            .ToArray();

        Assert.Equal(21, _fixture.Db.InsertScoreChanges(changes));

        using var connection = _fixture.DataSource.OpenConnection();
        Assert.Equal(22, ScalarInt(connection, "SELECT COUNT(*) FROM score_history"));

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT old_score, new_score, old_rank, new_rank, accuracy, is_full_combo,
                   stars, season, season_rank, all_time_rank, difficulty
            FROM score_history
            WHERE account_id = 'acct-single'
            """;
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal(100_000, reader.GetInt32(0));
        Assert.Equal(123_456, reader.GetInt32(1));
        Assert.Equal(2, reader.GetInt32(2));
        Assert.Equal(1, reader.GetInt32(3));
        Assert.Equal(987_654, reader.GetInt32(4));
        Assert.True(reader.GetBoolean(5));
        Assert.Equal(6, reader.GetInt32(6));
        Assert.Equal(11, reader.GetInt32(7));
        Assert.Equal(7, reader.GetInt32(8));
        Assert.Equal(42, reader.GetInt32(9));
        Assert.Equal(3, reader.GetInt32(10));
        Assert.False(reader.Read());
    }

    [Fact]
    public void BandWritesRemainExactWithoutRetiredObservationSchema()
    {
        AssertRetiredObservationSchemaAbsent();

        var persistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>());

        var merged = persistence.UpsertBandEntries("song-1", "Band_Duets", [new BandLeaderboardEntry
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

        Assert.Equal(1, merged);

        using var connection = _fixture.DataSource.OpenConnection();
        Assert.Equal(1, ScalarInt(connection, "SELECT COUNT(*) FROM band_entries"));
        Assert.Equal(2, ScalarInt(connection, "SELECT COUNT(*) FROM band_member_stats"));
        Assert.Equal(2, ScalarInt(connection, "SELECT COUNT(*) FROM band_members"));

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT member_index, account_id, instrument_id, score, accuracy,
                   is_full_combo, stars, difficulty
            FROM band_member_stats
            ORDER BY member_index
            """;
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal(0, reader.GetInt32(0));
        Assert.Equal("acct-1", reader.GetString(1));
        Assert.Equal(0, reader.GetInt32(2));
        Assert.Equal(111_111, reader.GetInt32(3));
        Assert.Equal(987_654, reader.GetInt32(4));
        Assert.True(reader.GetBoolean(5));
        Assert.Equal(5, reader.GetInt32(6));
        Assert.Equal(3, reader.GetInt32(7));

        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("acct-2", reader.GetString(1));
        Assert.Equal(1, reader.GetInt32(2));
        Assert.Equal(101_111, reader.GetInt32(3));
        Assert.Equal(876_543, reader.GetInt32(4));
        Assert.False(reader.GetBoolean(5));
        Assert.Equal(5, reader.GetInt32(6));
        Assert.Equal(3, reader.GetInt32(7));
        Assert.False(reader.Read());
    }

    private void AssertRetiredObservationSchemaAbsent()
    {
        using var connection = _fixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                to_regclass('public.player_score_observations') IS NULL,
                to_regclass('public.player_score_observation_union') IS NULL
            """;
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
    }

    private static int ScalarInt(Npgsql.NpgsqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
