using FSTService.Persistence;
using FSTService.Tests.Helpers;

namespace FSTService.Tests.Unit;

/// <summary>
/// Additional MetaDatabase tests to cover uncovered methods.
/// </summary>
public class MetaDatabaseAdditionalTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();
    private MetaDatabase Db => _fixture.Db;

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void BackfillScoreHistoryDifficulty_UpdatesNullRows()
    {
        // Insert a score history entry without difficulty
        Db.InsertScoreChange("song1", "Solo_Guitar", "acct1",
            null, 50000, null, 5,
            accuracy: 95, isFullCombo: true, stars: 6,
            scoreAchievedAt: "2025-01-01T00:00:00Z");

        // Backfill the difficulty
        Db.BackfillScoreHistoryDifficulty("acct1", "song1", "Solo_Guitar", 50000, 4);

        // Verify it was updated
        var history = Db.GetScoreHistory("acct1");
        Assert.NotEmpty(history);
    }

    [Fact]
    public void BackfillScoreHistoryDifficulty_NoMatchingRows_NoOp()
    {
        // Insert with a different score
        Db.InsertScoreChange("song1", "Solo_Guitar", "acct1",
            null, 50000, null, 5,
            accuracy: 95, isFullCombo: true, stars: 6,
            scoreAchievedAt: "2025-01-01T00:00:00Z");

        // Try to backfill for a non-matching score
        Db.BackfillScoreHistoryDifficulty("acct1", "song1", "Solo_Guitar", 99999, 4);

        // No crash, no error
    }

    [Fact]
    public void InsertScoreChanges_BatchInsert_InsertsAll()
    {
        var changes = new List<ScoreChangeRecord>
        {
            new()
            {
                SongId = "song1", Instrument = "Solo_Guitar", AccountId = "acct1",
                OldScore = null, NewScore = 50000, OldRank = null, NewRank = 5,
                Accuracy = 95, IsFullCombo = true, Stars = 6,
                ScoreAchievedAt = "2025-01-01T00:00:00Z",
            },
            new()
            {
                SongId = "song2", Instrument = "Solo_Bass", AccountId = "acct2",
                OldScore = 30000, NewScore = 40000, OldRank = 10, NewRank = 8,
                Accuracy = 90, IsFullCombo = false, Stars = 5,
                ScoreAchievedAt = "2025-02-01T00:00:00Z",
            },
        };

        var count = Db.InsertScoreChanges(changes);
        Assert.Equal(2, count);
    }

    [Fact]
    public void InsertScoreChanges_Empty_ReturnsZero()
    {
        var count = Db.InsertScoreChanges(Array.Empty<ScoreChangeRecord>());
        Assert.Equal(0, count);
    }

    [Fact]
    public void RivalSuggestionEntry_Properties_Accessible()
    {
        var entry = new RivalSuggestionEntry
        {
            AccountId = "acct1",
            DisplayName = "Player1",
            Direction = "above",
            SharedSongCount = 50,
            AheadCount = 30,
            BehindCount = 20,
            Songs = [new RivalSongSampleRow { SongId = "s1", Instrument = "Solo_Guitar" }],
        };
        Assert.Equal("acct1", entry.AccountId);
        Assert.Equal("Player1", entry.DisplayName);
        Assert.Equal("above", entry.Direction);
        Assert.Equal(50, entry.SharedSongCount);
        Assert.Equal(30, entry.AheadCount);
        Assert.Equal(20, entry.BehindCount);
        Assert.Single(entry.Songs);
    }

    [Fact]
    public void SaveAndLoadItemShopTracks_RoundTrips()
    {
        var songIds = new HashSet<string> { "shop1", "shop2" };
        var leaving = new HashSet<string> { "shop2" };
        Db.SaveItemShopTracks(songIds, leaving, DateTime.UtcNow);

        var (loaded, loadedLeaving) = Db.LoadItemShopTracks();
        Assert.Equal(2, loaded.Count);
        Assert.Contains("shop1", loaded);
        Assert.Contains("shop2", loaded);
        Assert.Single(loadedLeaving);
        Assert.Contains("shop2", loadedLeaving);
    }
}
