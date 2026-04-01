using System.Text.Json;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class ScrapeTimePrecomputerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly MetaDatabase _metaDb;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly PathDataStore _pathDataStore;
    private readonly ScrapeTimePrecomputer _sut;

    public ScrapeTimePrecomputerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"precomp_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _metaDb = new MetaDatabase(
            Path.Combine(_tempDir, "meta.db"),
            Substitute.For<ILogger<MetaDatabase>>());
        _metaDb.EnsureSchema();

        _persistence = new GlobalLeaderboardPersistence(
            _tempDir, _metaDb,
            Substitute.For<ILoggerFactory>(),
            Substitute.For<ILogger<GlobalLeaderboardPersistence>>());
        _persistence.Initialize();

        _pathDataStore = new PathDataStore(Path.Combine(_tempDir, "core.db"));

        _sut = new ScrapeTimePrecomputer(
            _persistence, _metaDb, _pathDataStore,
            new ScrapeProgressTracker(),
            Substitute.For<ILogger<ScrapeTimePrecomputer>>(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    public void Dispose()
    {
        _persistence.Dispose();
        _metaDb.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private void SeedSong(string songId, string instrument, int maxScore, params (string AccountId, int Score)[] entries)
    {
        // Ensure core song row exists for PathDataStore
        EnsureSongRow(songId);
        _pathDataStore.UpdateMaxScores(songId, CreateMaxScores(instrument, maxScore), "hash");

        var db = _persistence.GetOrCreateInstrumentDb(instrument);
        var list = entries.Select(e => new LeaderboardEntry
        {
            AccountId = e.AccountId, Score = e.Score,
            Accuracy = 95, Stars = 5, Season = 3,
        }).ToList();
        db.UpsertEntries(songId, list);
        db.RecomputeAllRanks();
    }

    private void RegisterUser(string accountId)
    {
        _metaDb.InsertAccountIds(new[] { accountId });
        _metaDb.InsertAccountNames(new[] { (accountId, (string?)"TestUser") });
        _metaDb.RegisterUser("web-tracker", accountId);
    }

    private void InsertScoreHistory(string accountId, string songId, string instrument, int score)
    {
        _metaDb.InsertScoreChange(songId, instrument, accountId,
            null, score, null, 1, accuracy: 90, isFullCombo: false, stars: 4);
    }

    // ── Tests ────────────────────────────────────────────────────

    [Fact]
    public async Task PrecomputeAllAsync_EmptyDb_DoesNotThrow()
    {
        await _sut.PrecomputeAllAsync(CancellationToken.None);
        Assert.Equal(0, _sut.Count);
    }

    [Fact]
    public async Task PrecomputeAllAsync_RegisteredUser_ProducesPlayerEntry()
    {
        RegisterUser("user1");
        SeedSong("s1", "Solo_Guitar", 100000,
            ("user1", 95000), ("p2", 90000), ("p3", 85000));

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var result = _sut.TryGet("player:user1:::");
        Assert.NotNull(result);

        var json = JsonDocument.Parse(result.Value.Json);
        Assert.Equal("user1", json.RootElement.GetProperty("accountId").GetString());
        Assert.Equal("TestUser", json.RootElement.GetProperty("displayName").GetString());
        var scores = json.RootElement.GetProperty("scores");
        Assert.Equal(1, scores.GetArrayLength());

        var score = scores[0];
        Assert.Equal("s1", score.GetProperty("songId").GetString());
        Assert.Equal(95000, score.GetProperty("score").GetInt32());
        Assert.True(score.GetProperty("rank").GetInt32() > 0);
    }

    [Fact]
    public async Task PrecomputeAllAsync_PlayerWithInvalidScore_HasMinLeeway()
    {
        RegisterUser("user1");
        // maxScore=100000, user score=105000 → minLeeway = 5.0
        SeedSong("s1", "Solo_Guitar", 100000,
            ("user1", 105000), ("p2", 90000));

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var result = _sut.TryGet("player:user1:::");
        Assert.NotNull(result);

        var json = JsonDocument.Parse(result.Value.Json);
        var score = json.RootElement.GetProperty("scores")[0];
        Assert.Equal(5.0, score.GetProperty("minLeeway").GetDouble());
    }

    [Fact]
    public async Task PrecomputeAllAsync_PlayerWithFallbackScores_HasValidScores()
    {
        RegisterUser("user1");
        SeedSong("s1", "Solo_Guitar", 100000,
            ("user1", 106000), ("p2", 90000), ("p3", 99000));

        // Insert a historical valid score
        InsertScoreHistory("user1", "s1", "Solo_Guitar", 99000);

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var result = _sut.TryGet("player:user1:::");
        Assert.NotNull(result);

        var json = JsonDocument.Parse(result.Value.Json);
        var score = json.RootElement.GetProperty("scores")[0];

        // Should have validScores array with the 99000 fallback
        Assert.True(score.TryGetProperty("validScores", out var validScores));
        Assert.True(validScores.GetArrayLength() > 0);

        var fallback = validScores[0];
        Assert.Equal(99000, fallback.GetProperty("score").GetInt32());
        Assert.True(fallback.GetProperty("minLeeway").GetDouble() <= 0);
    }

    [Fact]
    public async Task PrecomputeAllAsync_ProducesLeaderboardAllEntries()
    {
        SeedSong("s1", "Solo_Guitar", 100000,
            ("p1", 95000), ("p2", 90000), ("p3", 85000));

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        // Should have leaderboard-all entries for the song
        var noLeeway = _sut.TryGet("lb:s1:10:");
        Assert.NotNull(noLeeway);

        var json = JsonDocument.Parse(noLeeway.Value.Json);
        Assert.Equal("s1", json.RootElement.GetProperty("songId").GetString());

        var lb1 = _sut.TryGet("lb:s1:10:1");
        Assert.NotNull(lb1);
    }

    [Fact]
    public async Task PrecomputeAllAsync_ProducesPopulationTiers()
    {
        SeedSong("s1", "Solo_Guitar", 100000,
            ("p1", 105000), ("p2", 101000), ("p3", 99000), ("p4", 90000));

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var tiers = _sut.GetPopulationTiers();
        Assert.NotNull(tiers);
        Assert.True(tiers.ContainsKey(("s1", "Solo_Guitar")));

        var tierData = tiers[("s1", "Solo_Guitar")];
        // baseCount = entries with score <= 95000 → p4 (90000) = 1
        Assert.Equal(1, tierData.BaseCount);
        // tiers should have changepoints for the scores in the band
        Assert.True(tierData.Tiers.Count > 0);
    }

    [Fact]
    public void InvalidateAll_ClearsEverything()
    {
        // Manually store something
        _sut.PrecomputeUser("nonexistent"); // no-op but exercises the method
        _sut.InvalidateAll();
        Assert.Equal(0, _sut.Count);
        Assert.Null(_sut.GetPopulationTiers());
    }

    [Fact]
    public void TryGet_NonExistentKey_ReturnsNull()
    {
        Assert.Null(_sut.TryGet("player:unknown:::"));
    }

    [Fact]
    public async Task PrecomputeAllAsync_MultipleUsers_PrecomputesAll()
    {
        RegisterUser("user1");
        RegisterUser("user2");
        SeedSong("s1", "Solo_Guitar", 100000,
            ("user1", 95000), ("user2", 88000), ("p3", 80000));

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        Assert.NotNull(_sut.TryGet("player:user1:::"));
        Assert.NotNull(_sut.TryGet("player:user2:::"));
    }

    [Fact]
    public async Task PrecomputeAllAsync_EvictsUnregisteredPlayerEntries()
    {
        RegisterUser("user1");
        RegisterUser("user2");
        SeedSong("s1", "Solo_Guitar", 100000,
            ("user1", 95000), ("user2", 88000));

        await _sut.PrecomputeAllAsync(CancellationToken.None);
        Assert.NotNull(_sut.TryGet("player:user1:::"));
        Assert.NotNull(_sut.TryGet("player:user2:::"));

        // Unregister user2 between scrapes
        _metaDb.UnregisterUser("web-tracker", "user2");

        // Re-precompute — user2's entry should be evicted
        await _sut.PrecomputeAllAsync(CancellationToken.None);
        Assert.NotNull(_sut.TryGet("player:user1:::"));
        Assert.Null(_sut.TryGet("player:user2:::"));
    }

    [Fact]
    public async Task PrecomputeAllAsync_RankTiers_IncludesChangepoints()
    {
        RegisterUser("user1");
        SeedSong("s1", "Solo_Guitar", 100000,
            ("user1", 106000),
            ("p2", 103000),  // leeway = 3.0
            ("p3", 101000),  // leeway = 1.0
            ("p4", 99000),   // leeway = -1.0
            ("p5", 90000));

        // user1 has a valid fallback in history
        InsertScoreHistory("user1", "s1", "Solo_Guitar", 98000);

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var result = _sut.TryGet("player:user1:::");
        Assert.NotNull(result);

        var json = JsonDocument.Parse(result.Value.Json);
        var validScores = json.RootElement.GetProperty("scores")[0].GetProperty("validScores");
        var fallback = validScores[0];

        // Should have rankTiers
        Assert.True(fallback.TryGetProperty("rankTiers", out var rankTiers));
        Assert.True(rankTiers.GetArrayLength() > 0);

        // Each tier should have leeway and rank
        var firstTier = rankTiers[0];
        Assert.True(firstTier.TryGetProperty("leeway", out _));
        Assert.True(firstTier.TryGetProperty("rank", out _));
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrips_Correctly()
    {
        RegisterUser("user1");
        SeedSong("s1", "Solo_Guitar", 100000,
            ("user1", 95000), ("p2", 90000));

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var precomputeDir = Path.Combine(_tempDir, "precomputed");
        await _sut.SaveToDiskAsync(precomputeDir);

        // Verify files were created
        Assert.True(File.Exists(Path.Combine(precomputeDir, "responses.json.gz")));
        Assert.True(File.Exists(Path.Combine(precomputeDir, "population-tiers.json.gz")));

        // Create a fresh precomputer and load
        var sut2 = new ScrapeTimePrecomputer(
            _persistence, _metaDb, _pathDataStore,
            new ScrapeProgressTracker(),
            Substitute.For<ILogger<ScrapeTimePrecomputer>>(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(0, sut2.Count);
        var loaded = await sut2.LoadFromDiskAsync(precomputeDir);
        Assert.True(loaded);
        Assert.True(sut2.Count > 0);

        // Verify the player response round-trips
        var original = _sut.TryGet("player:user1:::");
        var restored = sut2.TryGet("player:user1:::");
        Assert.NotNull(original);
        Assert.NotNull(restored);
        Assert.Equal(original.Value.ETag, restored.Value.ETag);

        // Verify population tiers round-trip
        Assert.NotNull(sut2.GetPopulationTiers());
    }

    [Fact]
    public async Task LoadFromDisk_MissingDirectory_ReturnsFalse()
    {
        var loaded = await _sut.LoadFromDiskAsync(Path.Combine(_tempDir, "nonexistent"));
        Assert.False(loaded);
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static SongMaxScores CreateMaxScores(string instrument, int maxScore)
    {
        var ms = new SongMaxScores();
        switch (instrument)
        {
            case "Solo_Guitar": ms.MaxLeadScore = maxScore; break;
            case "Solo_Bass": ms.MaxBassScore = maxScore; break;
            case "Solo_Drums": ms.MaxDrumsScore = maxScore; break;
            case "Solo_Vocals": ms.MaxVocalsScore = maxScore; break;
            case "Solo_PeripheralGuitar": ms.MaxProLeadScore = maxScore; break;
            case "Solo_PeripheralBass": ms.MaxProBassScore = maxScore; break;
        }
        return ms;
    }

    private void EnsureSongRow(string songId)
    {
        // PathDataStore uses a private _connectionString field for its DB path.
        var connField = typeof(PathDataStore)
            .GetField("_connectionString", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var connStr = (string)connField.GetValue(_pathDataStore)!;

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Songs (
                SongId TEXT PRIMARY KEY,
                Title TEXT,
                MaxLeadScore INTEGER,
                MaxBassScore INTEGER,
                MaxDrumsScore INTEGER,
                MaxVocalsScore INTEGER,
                MaxProLeadScore INTEGER,
                MaxProBassScore INTEGER,
                DatFileHash TEXT,
                SongLastModified TEXT,
                PathsGeneratedAt TEXT,
                CHOptVersion TEXT
            );
            INSERT OR IGNORE INTO Songs (SongId, Title) VALUES (@songId, 'Test Song');
            """;
        cmd.Parameters.AddWithValue("@songId", songId);
        cmd.ExecuteNonQuery();
    }
}
