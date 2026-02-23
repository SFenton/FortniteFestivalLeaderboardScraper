using System.Text.Json;

namespace PercentileService.Tests;

public sealed class DtoTests
{
    [Fact]
    public void PercentileEntry_properties_are_settable()
    {
        var entry = new PercentileEntry
        {
            SongId = "song1",
            Instrument = "Solo_Guitar",
            Rank = 19,
            Score = 696274,
            Percentile = 1.378e-05,
            TotalEntries = 1378810,
        };

        Assert.Equal("song1", entry.SongId);
        Assert.Equal("Solo_Guitar", entry.Instrument);
        Assert.Equal(19, entry.Rank);
        Assert.Equal(696274, entry.Score);
        Assert.Equal(1.378e-05, entry.Percentile);
        Assert.Equal(1378810, entry.TotalEntries);
    }

    [Fact]
    public void PercentileEntry_defaults()
    {
        var entry = new PercentileEntry();
        Assert.Equal("", entry.SongId);
        Assert.Equal("", entry.Instrument);
        Assert.Equal(0, entry.Rank);
        Assert.Equal(0, entry.Score);
        Assert.Equal(0.0, entry.Percentile);
        Assert.Equal(0, entry.TotalEntries);
    }

    [Fact]
    public void LeaderboardPopulationItem_json_serialization()
    {
        var item = new LeaderboardPopulationItem
        {
            SongId = "testSong",
            Instrument = "Solo_Drums",
            TotalEntries = 42000,
        };

        var json = JsonSerializer.Serialize(item);
        Assert.Contains("\"songId\"", json);
        Assert.Contains("\"instrument\"", json);
        Assert.Contains("\"totalEntries\"", json);
        Assert.Contains("42000", json);
    }

    [Fact]
    public void LeaderboardPopulationItem_json_deserialization()
    {
        var json = """{"songId":"s1","instrument":"Solo_Bass","totalEntries":99}""";
        var item = JsonSerializer.Deserialize<LeaderboardPopulationItem>(json);

        Assert.NotNull(item);
        Assert.Equal("s1", item.SongId);
        Assert.Equal("Solo_Bass", item.Instrument);
        Assert.Equal(99, item.TotalEntries);
    }

    [Fact]
    public void PlayerProfileResponse_properties()
    {
        var resp = new PlayerProfileResponse
        {
            AccountId = "a1",
            DisplayName = "Test",
            TotalScores = 5,
            Scores = [new PlayerScoreItem { SongId = "s", Instrument = "i", Score = 100 }],
        };

        Assert.Equal("a1", resp.AccountId);
        Assert.Equal("Test", resp.DisplayName);
        Assert.Equal(5, resp.TotalScores);
        Assert.Single(resp.Scores);
    }

    [Fact]
    public void PlayerEntry_properties()
    {
        var entry = new PlayerEntry { SongId = "s1", Instrument = "Solo_Guitar" };
        Assert.Equal("s1", entry.SongId);
        Assert.Equal("Solo_Guitar", entry.Instrument);
    }

    [Fact]
    public void PlayerScoreItem_properties()
    {
        var item = new PlayerScoreItem { SongId = "s", Instrument = "i", Score = 42 };
        Assert.Equal("s", item.SongId);
        Assert.Equal("i", item.Instrument);
        Assert.Equal(42, item.Score);
    }

    [Fact]
    public void StoredPercentileCredentials_properties()
    {
        var creds = new StoredPercentileCredentials
        {
            AccountId = "acct",
            DisplayName = "User",
            RefreshToken = "refresh",
            SavedAt = "2026-01-01T00:00:00Z",
        };

        Assert.Equal("acct", creds.AccountId);
        Assert.Equal("User", creds.DisplayName);
        Assert.Equal("refresh", creds.RefreshToken);
        Assert.Equal("2026-01-01T00:00:00Z", creds.SavedAt);
    }

    [Fact]
    public void StoredPercentileCredentials_roundtrip_serialization()
    {
        var creds = new StoredPercentileCredentials
        {
            AccountId = "a1",
            DisplayName = "D1",
            RefreshToken = "r1",
            SavedAt = "2026-02-20T00:00:00Z",
        };

        var json = JsonSerializer.Serialize(creds);
        var deserialized = JsonSerializer.Deserialize<StoredPercentileCredentials>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("a1", deserialized.AccountId);
        Assert.Equal("D1", deserialized.DisplayName);
        Assert.Equal("r1", deserialized.RefreshToken);
        Assert.Equal("2026-02-20T00:00:00Z", deserialized.SavedAt);
    }
}
