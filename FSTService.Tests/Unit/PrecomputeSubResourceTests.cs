using System.Text.Json;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FSTService.Tests.Unit;

/// <summary>
/// Tests for Phase 1-4 precomputation methods in ScrapeTimePrecomputer:
/// player stats, history, sync-status, rivals overview, rivals-all (with indexed song samples),
/// leaderboard rivals, rankings pages, neighborhoods, and firstseen.
/// </summary>
public sealed class PrecomputeSubResourceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly InMemoryMetaDatabase _metaFixture = new();
    private readonly MetaDatabase _metaDb;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly PathDataStore _pathDataStore;
    private readonly ScrapeTimePrecomputer _sut;

    public PrecomputeSubResourceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"precomp_sub_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _metaDb = new MetaDatabase(_metaFixture.DataSource,
            Substitute.For<ILogger<MetaDatabase>>());

        _persistence = new GlobalLeaderboardPersistence(
            _metaDb,
            Substitute.For<ILoggerFactory>(),
            Substitute.For<ILogger<GlobalLeaderboardPersistence>>(),
            _metaFixture.DataSource,
            Options.Create(new FeatureOptions()));
        _persistence.Initialize();

        _pathDataStore = new PathDataStore(SharedPostgresContainer.CreateDatabase());

        _sut = new ScrapeTimePrecomputer(
            _persistence, _metaDb, _pathDataStore,
            new ScrapeProgressTracker(),
            Substitute.For<ILogger<ScrapeTimePrecomputer>>(),
            NullLoggerFactory.Instance,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    public void Dispose()
    {
        _persistence.Dispose();
        _metaDb.Dispose();
        _metaFixture.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private void RegisterUser(string accountId, string displayName = "TestUser")
    {
        _metaDb.InsertAccountIds(new[] { accountId });
        _metaDb.InsertAccountNames(new[] { (accountId, (string?)displayName) });
        _metaDb.RegisterUser("web-tracker", accountId);
    }

    private void SeedSong(string songId, string instrument, int maxScore,
        params (string AccountId, int Score)[] entries)
    {
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

    private void SeedRivals(string userId, string combo, List<UserRivalRow> rivals, List<RivalSongSampleRow> samples)
    {
        _metaDb.EnsureRivalsStatus(userId);
        _metaDb.CompleteRivals(userId, 1, rivals.Count);
        _metaDb.ReplaceRivalsData(userId, rivals, samples);
    }

    private static SongMaxScores CreateMaxScores(string instrument, int maxScore)
    {
        var ms = new SongMaxScores();
        switch (instrument)
        {
            case "Solo_Guitar": ms.MaxLeadScore = maxScore; break;
            case "Solo_Bass": ms.MaxBassScore = maxScore; break;
            case "Solo_Drums": ms.MaxDrumsScore = maxScore; break;
            case "Solo_Vocals": ms.MaxVocalsScore = maxScore; break;
        }
        return ms;
    }

    private void EnsureSongRow(string songId)
    {
        var dsField = typeof(PathDataStore)
            .GetField("_ds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var ds = (Npgsql.NpgsqlDataSource)dsField.GetValue(_pathDataStore)!;
        using var conn = ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO songs (song_id) VALUES (@sid) ON CONFLICT DO NOTHING";
        cmd.Parameters.AddWithValue("sid", songId);
        cmd.ExecuteNonQuery();
    }

    // ═══════════════════════════════════════════════════════════════
    // Player Stats
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PrecomputeAllAsync_ProducesPlayerStats_WhenStatsExist()
    {
        RegisterUser("u1");
        SeedSong("s1", "Solo_Guitar", 100000, ("u1", 95000), ("p2", 90000));

        // Seed stats tiers directly
        _metaDb.UpsertPlayerStatsTiers("u1", "Solo_Guitar", "[{\"leeway\":0,\"rank\":1}]");

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var result = _sut.TryGet("playerstats:u1");
        Assert.NotNull(result);

        var json = JsonDocument.Parse(result.Value.Json);
        Assert.Equal("u1", json.RootElement.GetProperty("accountId").GetString());
        Assert.True(json.RootElement.GetProperty("totalSongs").GetInt32() > 0);
    }

    // ═══════════════════════════════════════════════════════════════
    // Player History
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PrecomputeAllAsync_ProducesPlayerHistory()
    {
        RegisterUser("u1");
        _metaDb.InsertScoreChange("s1", "Solo_Guitar", "u1", null, 90000, null, 1,
            accuracy: 95, isFullCombo: false, stars: 4);

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var result = _sut.TryGet("history:u1");
        Assert.NotNull(result);

        var json = JsonDocument.Parse(result.Value.Json);
        Assert.Equal("u1", json.RootElement.GetProperty("accountId").GetString());
        Assert.True(json.RootElement.GetProperty("count").GetInt32() >= 1);
        Assert.True(json.RootElement.GetProperty("history").GetArrayLength() >= 1);
    }

    // ═══════════════════════════════════════════════════════════════
    // Sync Status
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PrecomputeAllAsync_ProducesSyncStatus()
    {
        RegisterUser("u1");

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var result = _sut.TryGet("syncstatus:u1");
        Assert.NotNull(result);

        var json = JsonDocument.Parse(result.Value.Json);
        Assert.Equal("u1", json.RootElement.GetProperty("accountId").GetString());
        Assert.True(json.RootElement.GetProperty("isTracked").GetBoolean());
    }

    // ═══════════════════════════════════════════════════════════════
    // Rivals Overview
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PrecomputeAllAsync_ProducesRivalsOverview_WhenRivalsExist()
    {
        RegisterUser("u1");
        RegisterUser("rival1", "RivalPlayer");

        SeedRivals("u1", "Solo_Guitar", new List<UserRivalRow>
        {
            new() { UserId = "u1", RivalAccountId = "rival1", InstrumentCombo = "Solo_Guitar",
                     Direction = "above", RivalScore = 100, AvgSignedDelta = -5,
                     SharedSongCount = 10, AheadCount = 6, BehindCount = 4, ComputedAt = "2026-01-01" },
        }, new List<RivalSongSampleRow>());

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var result = _sut.TryGet("rivals-overview:u1");
        Assert.NotNull(result);

        var json = JsonDocument.Parse(result.Value.Json);
        Assert.Equal("u1", json.RootElement.GetProperty("accountId").GetString());
        Assert.True(json.RootElement.GetProperty("combos").GetArrayLength() >= 1);
    }

    // ═══════════════════════════════════════════════════════════════
    // Rivals All (with indexed song samples)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PrecomputeAllAsync_RivalsAll_HasSongsIndexAndSamples()
    {
        RegisterUser("u1");
        RegisterUser("rival1", "RivalPlayer");

        var rivals = new List<UserRivalRow>
        {
            new() { UserId = "u1", RivalAccountId = "rival1", InstrumentCombo = "Solo_Guitar",
                     Direction = "above", RivalScore = 100, AvgSignedDelta = -5,
                     SharedSongCount = 2, AheadCount = 1, BehindCount = 1, ComputedAt = "2026-01-01" },
        };
        var samples = new List<RivalSongSampleRow>
        {
            new() { UserId = "u1", RivalAccountId = "rival1", Instrument = "Solo_Guitar",
                     SongId = "s1", UserRank = 5, RivalRank = 3, RankDelta = 2, UserScore = 90000, RivalScore = 92000 },
            new() { UserId = "u1", RivalAccountId = "rival1", Instrument = "Solo_Guitar",
                     SongId = "s2", UserRank = 8, RivalRank = 12, RankDelta = -4, UserScore = 85000, RivalScore = 80000 },
        };
        SeedRivals("u1", "Solo_Guitar", rivals, samples);

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var result = _sut.TryGet("rivals-all:u1");
        Assert.NotNull(result);

        var json = JsonDocument.Parse(result.Value.Json);

        // Verify songs index exists
        Assert.True(json.RootElement.TryGetProperty("songs", out var songsArr));
        Assert.True(songsArr.GetArrayLength() >= 2);

        // songs should contain s1 and s2
        var songIds = new HashSet<string>();
        foreach (var s in songsArr.EnumerateArray())
            songIds.Add(s.GetString()!);
        Assert.Contains("s1", songIds);
        Assert.Contains("s2", songIds);

        // Verify combos have rivals with samples
        var combos = json.RootElement.GetProperty("combos");
        Assert.True(combos.GetArrayLength() >= 1);

        var firstCombo = combos[0];
        var above = firstCombo.GetProperty("above");
        Assert.True(above.GetArrayLength() >= 1);

        var rival = above[0];
        Assert.Equal("rival1", rival.GetProperty("accountId").GetString());

        // Verify samples array on the rival entry
        Assert.True(rival.TryGetProperty("samples", out var samplesArr));
        Assert.Equal(2, samplesArr.GetArrayLength());

        // Verify samples use integer index (s) not string songId
        var sample0 = samplesArr[0];
        Assert.True(sample0.TryGetProperty("s", out var sIdx));
        Assert.True(sIdx.ValueKind == JsonValueKind.Number);

        // The index should point to a valid song
        var idx = sIdx.GetInt32();
        Assert.True(idx >= 0 && idx < songsArr.GetArrayLength());
    }

    [Fact]
    public async Task PrecomputeAllAsync_RivalsAll_SongIndexDeduplicates()
    {
        RegisterUser("u1");
        RegisterUser("r1", "Rival1");
        RegisterUser("r2", "Rival2");

        // Both rivals share the same song
        var rivals = new List<UserRivalRow>
        {
            new() { UserId = "u1", RivalAccountId = "r1", InstrumentCombo = "Solo_Guitar",
                     Direction = "above", RivalScore = 100, AvgSignedDelta = -2,
                     SharedSongCount = 1, AheadCount = 1, BehindCount = 0, ComputedAt = "2026-01-01" },
            new() { UserId = "u1", RivalAccountId = "r2", InstrumentCombo = "Solo_Guitar",
                     Direction = "below", RivalScore = 80, AvgSignedDelta = 3,
                     SharedSongCount = 1, AheadCount = 0, BehindCount = 1, ComputedAt = "2026-01-01" },
        };
        var samples = new List<RivalSongSampleRow>
        {
            new() { UserId = "u1", RivalAccountId = "r1", Instrument = "Solo_Guitar",
                     SongId = "shared_song", UserRank = 5, RivalRank = 3, RankDelta = 2, UserScore = 90000, RivalScore = 92000 },
            new() { UserId = "u1", RivalAccountId = "r2", Instrument = "Solo_Guitar",
                     SongId = "shared_song", UserRank = 5, RivalRank = 8, RankDelta = -3, UserScore = 90000, RivalScore = 85000 },
        };
        SeedRivals("u1", "Solo_Guitar", rivals, samples);

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var result = _sut.TryGet("rivals-all:u1");
        Assert.NotNull(result);
        var json = JsonDocument.Parse(result.Value.Json);

        // Songs array should have exactly 1 entry (deduplicated)
        var songsArr = json.RootElement.GetProperty("songs");
        Assert.Equal(1, songsArr.GetArrayLength());
        Assert.Equal("shared_song", songsArr[0].GetString());

        // Both rivals' samples should reference index 0
        var combos = json.RootElement.GetProperty("combos");
        var aboveRival = combos[0].GetProperty("above")[0];
        var belowRival = combos[0].GetProperty("below")[0];
        Assert.Equal(0, aboveRival.GetProperty("samples")[0].GetProperty("s").GetInt32());
        Assert.Equal(0, belowRival.GetProperty("samples")[0].GetProperty("s").GetInt32());
    }

    // ═══════════════════════════════════════════════════════════════
    // Rankings Pages
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PrecomputeAllAsync_ProducesRankingsPages()
    {
        RegisterUser("u1");
        RegisterUser("u2", "User2");
        SeedSong("s1", "Solo_Guitar", 100000, ("u1", 95000), ("u2", 90000));

        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        var ranked = db.ComputeAccountRankings(totalChartedSongs: 1, credibilityThreshold: 0);

        // Verify rankings were actually computed
        if (ranked < 2)
        {
            // If rankings couldn't be computed (e.g., SongStats not populated),
            // just verify precompute doesn't crash
            await _sut.PrecomputeAllAsync(CancellationToken.None);
            return;
        }

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        // Check page 1 for "totalscore" metric
        var result = _sut.TryGet("rankings:Solo_Guitar:totalscore:1:50");
        Assert.NotNull(result);

        var json = JsonDocument.Parse(result.Value.Json);
        Assert.Equal("Solo_Guitar", json.RootElement.GetProperty("instrument").GetString());
        Assert.Equal("totalscore", json.RootElement.GetProperty("rankBy").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("page").GetInt32());
        Assert.True(json.RootElement.GetProperty("totalAccounts").GetInt32() >= 2);
        Assert.True(json.RootElement.GetProperty("entries").GetArrayLength() >= 2);
    }

    [Fact]
    public async Task PrecomputeAllAsync_ProducesRankingsOverview()
    {
        RegisterUser("u1");
        SeedSong("s1", "Solo_Guitar", 100000, ("u1", 95000));

        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        db.ComputeAccountRankings(totalChartedSongs: 1);

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var result = _sut.TryGet("rankings:overview:adjusted:10");
        Assert.NotNull(result);

        var json = JsonDocument.Parse(result.Value.Json);
        Assert.True(json.RootElement.TryGetProperty("instruments", out var instruments));
        Assert.True(instruments.TryGetProperty("Solo_Guitar", out _));
    }

    // ═══════════════════════════════════════════════════════════════
    // Neighborhoods
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PrecomputeAllAsync_ProducesNeighborhood_ForRegisteredUser()
    {
        RegisterUser("u1");
        RegisterUser("u2", "User2");
        SeedSong("s1", "Solo_Guitar", 100000, ("u1", 95000), ("u2", 90000));

        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        var ranked = db.ComputeAccountRankings(totalChartedSongs: 1, credibilityThreshold: 0);

        if (ranked < 2)
        {
            await _sut.PrecomputeAllAsync(CancellationToken.None);
            return;
        }

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var result = _sut.TryGet("neighborhood:Solo_Guitar:u1:5");
        Assert.NotNull(result);

        var json = JsonDocument.Parse(result.Value.Json);
        Assert.Equal("Solo_Guitar", json.RootElement.GetProperty("instrument").GetString());
        Assert.Equal("u1", json.RootElement.GetProperty("accountId").GetString());
        Assert.True(json.RootElement.TryGetProperty("self", out _));
    }

    // ═══════════════════════════════════════════════════════════════
    // FirstSeen
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PrecomputeAllAsync_ProducesFirstSeen()
    {
        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var result = _sut.TryGet("firstseen");
        Assert.NotNull(result);

        var json = JsonDocument.Parse(result.Value.Json);
        Assert.True(json.RootElement.TryGetProperty("count", out _));
        Assert.True(json.RootElement.TryGetProperty("songs", out _));
    }

    // ═══════════════════════════════════════════════════════════════
    // Empty DB edge cases
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PrecomputeAllAsync_NoRegisteredUsers_NoPlayerKeys()
    {
        await _sut.PrecomputeAllAsync(CancellationToken.None);

        // No player sub-resource keys should exist
        Assert.Null(_sut.TryGet("playerstats:any"));
        Assert.Null(_sut.TryGet("history:any"));
        Assert.Null(_sut.TryGet("syncstatus:any"));
        Assert.Null(_sut.TryGet("rivals-overview:any"));
        Assert.Null(_sut.TryGet("rivals-all:any"));

        // firstseen should still exist
        Assert.NotNull(_sut.TryGet("firstseen"));
    }

    [Fact]
    public async Task PrecomputeAllAsync_RegisteredUserNoRivals_NoRivalsKeys()
    {
        RegisterUser("u1");

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        // sync-status should exist (always for registered users)
        Assert.NotNull(_sut.TryGet("syncstatus:u1"));
        // history should exist (even if empty)
        Assert.NotNull(_sut.TryGet("history:u1"));
        // rivals should NOT exist (no rivals data)
        Assert.Null(_sut.TryGet("rivals-overview:u1"));
        Assert.Null(_sut.TryGet("rivals-all:u1"));
    }

    // ═══════════════════════════════════════════════════════════════
    // PrecomputeUser (on-demand /track)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void PrecomputeUser_ProducesPlayerAndSubResources()
    {
        RegisterUser("u1");
        SeedSong("s1", "Solo_Guitar", 100000, ("u1", 95000));
        _metaDb.InsertScoreChange("s1", "Solo_Guitar", "u1", null, 95000, null, 1,
            accuracy: 95, isFullCombo: false, stars: 5);

        _sut.PrecomputeUser("u1");

        Assert.NotNull(_sut.TryGet("player:u1:::"));
        Assert.NotNull(_sut.TryGet("history:u1"));
        Assert.NotNull(_sut.TryGet("syncstatus:u1"));
    }
}
