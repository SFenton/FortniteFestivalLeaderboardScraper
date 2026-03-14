using FSTService.Scraping;
using Microsoft.Data.Sqlite;

namespace FSTService.Tests.Unit;

public sealed class PathDataStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly PathDataStore _store;

    public PathDataStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"pathdata-test-{Guid.NewGuid():N}.db");
        CreateTestDb();
        _store = new PathDataStore(_dbPath);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    private void CreateTestDb()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE Songs (
                SongId TEXT PRIMARY KEY,
                Title TEXT,
                MaxLeadScore INTEGER,
                MaxBassScore INTEGER,
                MaxDrumsScore INTEGER,
                MaxVocalsScore INTEGER,
                MaxProLeadScore INTEGER,
                MaxProBassScore INTEGER,
                DatFileHash TEXT,
                PathsGeneratedAt TEXT,
                CHOptVersion TEXT
            );
            INSERT INTO Songs (SongId, Title) VALUES ('song1', 'Test Song 1');
            INSERT INTO Songs (SongId, Title) VALUES ('song2', 'Test Song 2');
            INSERT INTO Songs (SongId, Title, DatFileHash) VALUES ('song3', 'Test Song 3', 'abc123');
            """;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void GetDatFileHashes_returns_only_songs_with_hashes()
    {
        var hashes = _store.GetDatFileHashes();

        Assert.Single(hashes);
        Assert.True(hashes.ContainsKey("song3"));
        Assert.Equal("abc123", hashes["song3"]);
    }

    [Fact]
    public void GetAllMaxScores_returns_empty_when_no_scores()
    {
        var scores = _store.GetAllMaxScores();
        Assert.Empty(scores);
    }

    [Fact]
    public void UpdateMaxScores_then_GetAllMaxScores_returns_data()
    {
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
        var scores = new SongMaxScores { MaxLeadScore = 50000 };
        _store.UpdateMaxScores("song1", scores, "hash_abc");

        var hashes = _store.GetDatFileHashes();
        Assert.Equal("hash_abc", hashes["song1"]);
    }

    [Fact]
    public void ClearMaxScores_removes_scores_and_hash()
    {
        var scores = new SongMaxScores
        {
            MaxLeadScore = 100000,
            GeneratedAt = "2026-01-01T00:00:00Z",
        };
        _store.UpdateMaxScores("song2", scores, "somehash");
        Assert.Single(_store.GetAllMaxScores());

        _store.ClearMaxScores("song2");

        Assert.Empty(_store.GetAllMaxScores());
        Assert.DoesNotContain("song2", (IDictionary<string, string>)_store.GetDatFileHashes());
    }

    [Fact]
    public void UpdateMaxScores_partial_null_scores()
    {
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
