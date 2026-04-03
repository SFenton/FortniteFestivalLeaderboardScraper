using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;

namespace FSTService.Tests.Unit;

public sealed class PathDataStoreTests : IDisposable
{
    private readonly Npgsql.NpgsqlDataSource _ds;
    private readonly PathDataStore _store;

    public PathDataStoreTests()
    {
        _ds = SharedPostgresContainer.CreateDatabase();
        _store = new PathDataStore(_ds);
    }

    public void Dispose()
    {
        _ds.Dispose();
    }

    private void EnsureSongRow(string songId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"INSERT INTO songs (song_id) VALUES ('{songId}') ON CONFLICT DO NOTHING;";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void GetPathGenerationState_returns_only_songs_with_hashes()
    {
        EnsureSongRow("song1");
        EnsureSongRow("song2");
        EnsureSongRow("song3");
        _store.UpdateMaxScores("song3", new SongMaxScores { MaxLeadScore = 50000 }, "abc123");

        var state = _store.GetPathGenerationState();

        Assert.Single(state);
        Assert.True(state.ContainsKey("song3"));
        Assert.Equal("abc123", state["song3"].Hash);
    }

    [Fact]
    public void GetAllMaxScores_returns_empty_when_no_scores()
    {
        var scores = _store.GetAllMaxScores();
        Assert.Empty(scores);
    }

    [Fact]
    public void GetPathGenerationState_returns_hash_and_lastModified()
    {
        EnsureSongRow("song1");
        var scores = new SongMaxScores { MaxLeadScore = 50000 };
        _store.UpdateMaxScores("song1", scores, "hash1", "2026-01-01T00:00:00Z");

        var state = _store.GetPathGenerationState();
        Assert.True(state.ContainsKey("song1"));
        Assert.Equal("hash1", state["song1"].Hash);
        Assert.Equal("2026-01-01T00:00:00Z", state["song1"].LastModified);
    }

    [Fact]
    public void GetPathGenerationState_returns_null_lastModified_when_not_set()
    {
        EnsureSongRow("song1");
        var scores = new SongMaxScores { MaxLeadScore = 50000 };
        _store.UpdateMaxScores("song1", scores, "hash1");

        var state = _store.GetPathGenerationState();
        Assert.True(state.ContainsKey("song1"));
        Assert.Equal("hash1", state["song1"].Hash);
        Assert.Null(state["song1"].LastModified);
    }

    [Fact]
    public void UpdateMaxScores_then_GetAllMaxScores_returns_data()
    {
        EnsureSongRow("song1");
        var scores = new SongMaxScores
        {
            MaxLeadScore = 100000,
            MaxBassScore = 80000,
            MaxDrumsScore = 120000,
            MaxVocalsScore = 70000,
            MaxProLeadScore = 110000,
            MaxProBassScore = 90000,
            GeneratedAt = "2026-01-01T00:00:00Z",
            CHOptVersion = "1.10.3",
        };

        _store.UpdateMaxScores("song1", scores, "newhash");

        var all = _store.GetAllMaxScores();
        Assert.Single(all);
        Assert.True(all.ContainsKey("song1"));
        Assert.Equal(100000, all["song1"].MaxLeadScore);
        Assert.Equal(80000, all["song1"].MaxBassScore);
        Assert.Equal(120000, all["song1"].MaxDrumsScore);
        Assert.Equal(70000, all["song1"].MaxVocalsScore);
        Assert.Equal(110000, all["song1"].MaxProLeadScore);
        Assert.Equal(90000, all["song1"].MaxProBassScore);
    }

    [Fact]
    public void UpdateMaxScores_updates_dat_file_hash()
    {
        EnsureSongRow("song1");
        var scores = new SongMaxScores { MaxLeadScore = 50000 };
        _store.UpdateMaxScores("song1", scores, "hash_abc");

        var state = _store.GetPathGenerationState();
        Assert.Equal("hash_abc", state["song1"].Hash);
    }

    [Fact]
    public void UpdateMaxScores_partial_null_scores()
    {
        EnsureSongRow("song1");
        var scores = new SongMaxScores
        {
            MaxLeadScore = 100000,
            MaxBassScore = null,
            MaxDrumsScore = 120000,
        };

        _store.UpdateMaxScores("song1", scores, "hash");
        var all = _store.GetAllMaxScores();

        Assert.Equal(100000, all["song1"].MaxLeadScore);
        Assert.Null(all["song1"].MaxBassScore);
        Assert.Equal(120000, all["song1"].MaxDrumsScore);
    }
}

public sealed class SongMaxScoresTests
{
    [Theory]
    [InlineData("Solo_Guitar", 100)]
    [InlineData("Solo_Bass", 200)]
    [InlineData("Solo_Drums", 300)]
    [InlineData("Solo_Vocals", 400)]
    [InlineData("Solo_PeripheralGuitar", 500)]
    [InlineData("Solo_PeripheralBass", 600)]
    public void GetByInstrument_returns_correct_score(string instrument, int score)
    {
        var ms = new SongMaxScores();
        ms.SetByInstrument(instrument, score);
        Assert.Equal(score, ms.GetByInstrument(instrument));
    }

    [Fact]
    public void GetByInstrument_unknown_returns_null()
    {
        var ms = new SongMaxScores { MaxLeadScore = 100 };
        Assert.Null(ms.GetByInstrument("Unknown_Instrument"));
    }

    [Fact]
    public void SetByInstrument_unknown_does_nothing()
    {
        var ms = new SongMaxScores();
        ms.SetByInstrument("Unknown_Instrument", 999);
        // Should not throw and no field should be set
        Assert.Null(ms.MaxLeadScore);
        Assert.Null(ms.MaxBassScore);
    }
}
