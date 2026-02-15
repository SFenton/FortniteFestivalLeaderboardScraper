using FSTService.Tests.Helpers;
using FSTService.Scraping;

namespace FSTService.Tests.Unit;

public sealed class InstrumentDatabaseTests : IDisposable
{
    private readonly TempInstrumentDatabase _fixture = new();
    private Persistence.InstrumentDatabase Db => _fixture.Db;

    public void Dispose() => _fixture.Dispose();

    private static LeaderboardEntry MakeEntry(string accountId, int score,
        int accuracy = 95, bool fc = false, int stars = 5, int season = 3) =>
        new()
        {
            AccountId = accountId,
            Score = score,
            Accuracy = accuracy,
            IsFullCombo = fc,
            Stars = stars,
            Season = season,
            Percentile = 99.0,
            EndTime = "2025-01-15T12:00:00Z",
        };

    // ═══ UpsertEntries ══════════════════════════════════════════

    [Fact]
    public void UpsertEntries_inserts_new_rows()
    {
        var entries = new List<LeaderboardEntry>
        {
            MakeEntry("acct_1", 100_000),
            MakeEntry("acct_2", 90_000),
        };

        var affected = Db.UpsertEntries("song_1", entries);
        Assert.Equal(2, affected);
        Assert.Equal(2, Db.GetTotalEntryCount());
    }

    [Fact]
    public void UpsertEntries_updates_changed_score()
    {
        Db.UpsertEntries("song_1", [MakeEntry("acct_1", 80_000)]);
        Db.UpsertEntries("song_1", [MakeEntry("acct_1", 100_000)]);

        var entry = Db.GetEntry("song_1", "acct_1");
        Assert.NotNull(entry);
        Assert.Equal(100_000, entry.Score);
        Assert.Equal(1, Db.GetTotalEntryCount());
    }

    [Fact]
    public void UpsertEntries_skips_identical_score()
    {
        Db.UpsertEntries("song_1", [MakeEntry("acct_1", 100_000)]);
        var affected = Db.UpsertEntries("song_1", [MakeEntry("acct_1", 100_000)]);
        Assert.Equal(0, affected);
    }

    [Fact]
    public void UpsertEntries_returns_zero_for_empty_list()
    {
        var affected = Db.UpsertEntries("song_1", []);
        Assert.Equal(0, affected);
    }

    // ═══ GetEntry ═══════════════════════════════════════════════

    [Fact]
    public void GetEntry_returns_entry_when_exists()
    {
        Db.UpsertEntries("song_1", [MakeEntry("acct_1", 100_000, accuracy: 98, fc: true, stars: 6)]);

        var entry = Db.GetEntry("song_1", "acct_1");
        Assert.NotNull(entry);
        Assert.Equal("acct_1", entry.AccountId);
        Assert.Equal(100_000, entry.Score);
        Assert.Equal(98, entry.Accuracy);
        Assert.True(entry.IsFullCombo);
        Assert.Equal(6, entry.Stars);
        Assert.Equal(3, entry.Season);
    }

    [Fact]
    public void GetEntry_returns_null_when_not_found()
    {
        var entry = Db.GetEntry("song_1", "nobody");
        Assert.Null(entry);
    }

    // ═══ GetLeaderboard ═════════════════════════════════════════

    [Fact]
    public void GetLeaderboard_orders_by_score_descending()
    {
        Db.UpsertEntries("song_1",
        [
            MakeEntry("acct_low",  50_000),
            MakeEntry("acct_mid",  75_000),
            MakeEntry("acct_high", 100_000),
        ]);

        var board = Db.GetLeaderboard("song_1");
        Assert.Equal(3, board.Count);
        Assert.Equal("acct_high", board[0].AccountId);
        Assert.Equal("acct_mid", board[1].AccountId);
        Assert.Equal("acct_low", board[2].AccountId);
    }

    [Fact]
    public void GetLeaderboard_respects_top_limit()
    {
        Db.UpsertEntries("song_1",
        [
            MakeEntry("a", 10_000),
            MakeEntry("b", 20_000),
            MakeEntry("c", 30_000),
        ]);

        var board = Db.GetLeaderboard("song_1", top: 2);
        Assert.Equal(2, board.Count);
        Assert.Equal("c", board[0].AccountId);
        Assert.Equal("b", board[1].AccountId);
    }

    [Fact]
    public void GetLeaderboard_returns_empty_for_unknown_song()
    {
        var board = Db.GetLeaderboard("nonexistent");
        Assert.Empty(board);
    }

    // ═══ GetPlayerScores ════════════════════════════════════════

    [Fact]
    public void GetPlayerScores_returns_all_songs_for_account()
    {
        Db.UpsertEntries("song_A", [MakeEntry("acct_1", 100_000)]);
        Db.UpsertEntries("song_B", [MakeEntry("acct_1", 90_000)]);
        Db.UpsertEntries("song_A", [MakeEntry("acct_2", 80_000)]);

        var scores = Db.GetPlayerScores("acct_1");
        Assert.Equal(2, scores.Count);
        Assert.All(scores, s => Assert.Equal("Solo_Guitar", s.Instrument));
    }

    [Fact]
    public void GetPlayerScores_returns_empty_for_unknown_account()
    {
        var scores = Db.GetPlayerScores("nobody");
        Assert.Empty(scores);
    }

    // ═══ GetAnySongId ═══════════════════════════════════════════

    [Fact]
    public void GetAnySongId_returns_null_when_empty()
    {
        var songId = Db.GetAnySongId();
        Assert.Null(songId);
    }

    [Fact]
    public void GetAnySongId_returns_song_when_data_exists()
    {
        Db.UpsertEntries("song_1", [MakeEntry("acct_1", 100_000)]);
        var songId = Db.GetAnySongId();
        Assert.Equal("song_1", songId);
    }

    // ═══ GetTotalEntryCount ═════════════════════════════════════

    [Fact]
    public void GetTotalEntryCount_counts_across_songs()
    {
        Db.UpsertEntries("song_A", [MakeEntry("acct_1", 100_000)]);
        Db.UpsertEntries("song_B", [MakeEntry("acct_1", 90_000), MakeEntry("acct_2", 80_000)]);

        Assert.Equal(3, Db.GetTotalEntryCount());
    }

    // ═══ EndTime ════════════════════════════════════════════════

    [Fact]
    public void Upsert_preserves_null_EndTime()
    {
        var entry = MakeEntry("acct_1", 100_000);
        entry.EndTime = null;
        Db.UpsertEntries("song_1", [entry]);

        var result = Db.GetEntry("song_1", "acct_1");
        Assert.NotNull(result);
        Assert.Null(result.EndTime);
    }
}
