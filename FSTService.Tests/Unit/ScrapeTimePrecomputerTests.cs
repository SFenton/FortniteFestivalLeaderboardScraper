using System.Text.Json;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class ScrapeTimePrecomputerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly InMemoryMetaDatabase _metaFixture = new();
    private readonly MetaDatabase _metaDb;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly PathDataStore _pathDataStore;
    private readonly ScrapeTimePrecomputer _sut;

    public ScrapeTimePrecomputerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"precomp_test_{Guid.NewGuid():N}");
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
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            new FeatureOptions());
    }

    public void Dispose()
    {
        _persistence.Dispose();
        _metaDb.Dispose();
        _metaFixture.Dispose();
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

    private void SeedBandSong(
        string songId,
        string bandType,
        params (string[] Members, string InstrumentCombo, int Score)[] entries)
    {
        var persistence = new BandLeaderboardPersistence(
            _metaFixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>());

        persistence.UpsertBandEntries(songId, bandType, entries.Select(entry =>
        {
            var sortedMembers = entry.Members.OrderBy(static member => member, StringComparer.OrdinalIgnoreCase).ToArray();
            var instrumentIds = entry.InstrumentCombo.Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Select(int.Parse)
                .ToArray();
            return new BandLeaderboardEntry
            {
                TeamKey = string.Join(':', sortedMembers),
                TeamMembers = sortedMembers,
                InstrumentCombo = entry.InstrumentCombo,
                Score = entry.Score,
                Accuracy = 950_000,
                IsFullCombo = true,
                Stars = 5,
                Difficulty = 3,
                Season = 1,
                Rank = 1,
                Percentile = 0.1,
                Source = "test",
                MemberStats = sortedMembers.Select((member, index) => new BandMemberStats
                {
                    MemberIndex = index,
                    AccountId = member,
                    InstrumentId = instrumentIds[index],
                    Score = entry.Score / sortedMembers.Length,
                    Accuracy = 950_000,
                    IsFullCombo = true,
                    Stars = 5,
                    Difficulty = 3,
                }).ToList(),
            };
        }).ToList());
    }

    private void InsertScoreHistory(string accountId, string songId, string instrument, int score)
    {
        _metaDb.InsertScoreChange(songId, instrument, accountId,
            null, score, null, 1, accuracy: 90, isFullCombo: false, stars: 4);
    }

    private void InsertSnapshotEntry(long snapshotId, string songId, string instrument, string accountId, int score)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO leaderboard_entries_snapshot
            (snapshot_id, song_id, instrument, account_id, score, accuracy, is_full_combo, stars,
             season, percentile, rank, source, difficulty, api_rank, end_time, first_seen_at, last_updated_at)
            VALUES
            (@snapshotId, @songId, @instrument, @accountId, @score, 95, false, 5,
             3, 99.0, 1, 'scrape', 3, 1, '2025-01-15T12:00:00Z', @now, @now)
            """;
        cmd.Parameters.AddWithValue("snapshotId", snapshotId);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("score", score);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    private void InsertSnapshotState(string songId, string instrument, long snapshotId)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO leaderboard_snapshot_state
            (song_id, instrument, active_snapshot_id, scrape_id, is_finalized, wave1_finalized_at, updated_at)
            VALUES (@songId, @instrument, @snapshotId, @snapshotId, TRUE, @now, @now)
            ON CONFLICT (song_id, instrument) DO UPDATE SET
                active_snapshot_id = EXCLUDED.active_snapshot_id,
                scrape_id = EXCLUDED.scrape_id,
                is_finalized = EXCLUDED.is_finalized,
                wave1_finalized_at = EXCLUDED.wave1_finalized_at,
                updated_at = EXCLUDED.updated_at
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("snapshotId", snapshotId);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    // ── Tests ────────────────────────────────────────────────────

    [Fact]
    public async Task PrecomputeAllAsync_EmptyDb_DoesNotThrow()
    {
        await _sut.PrecomputeAllAsync(CancellationToken.None);
        // Static data (firstseen) is always precomputed, even on empty DB
        Assert.True(_sut.Count >= 0);
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
        Assert.Equal("s1", score.GetProperty("si").GetString());
        Assert.Equal(95000, score.GetProperty("sc").GetInt32());
        Assert.True(score.GetProperty("rk").GetInt32() > 0);
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
        Assert.Equal(5.0, score.GetProperty("ml").GetDouble());
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
        Assert.True(score.TryGetProperty("vs", out var validScores));
        Assert.True(validScores.GetArrayLength() > 0);

        var fallback = validScores[0];
        Assert.Equal(99000, fallback.GetProperty("sc").GetInt32());
        Assert.True(fallback.GetProperty("ml").GetDouble() <= 0);
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
    public async Task PrecomputeAllAsync_ProducesSongBandLeaderboardAllEntries()
    {
        SeedBandSong("band-song-1", "Band_Duets",
            (["band-p1", "band-p2"], "0:1", 1_200),
            (["band-p3", "band-p4"], "2:3", 1_100));

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var result = _sut.TryGet(global::FSTService.LeaderboardCacheKeys.SongBandLeaderboardsAll("band-song-1", 10));
        Assert.NotNull(result);

        var json = JsonDocument.Parse(result.Value.Json);
        Assert.Equal("band-song-1", json.RootElement.GetProperty("songId").GetString());

        var duos = json.RootElement.GetProperty("bands")
            .EnumerateArray()
            .Single(band => band.GetProperty("bandType").GetString() == "Band_Duets");
        Assert.Equal(2, duos.GetProperty("count").GetInt32());
        Assert.Equal("band-p1:band-p2", duos.GetProperty("entries")[0].GetProperty("teamKey").GetString());
        Assert.Equal(1_200, duos.GetProperty("entries")[0].GetProperty("score").GetInt32());
        Assert.Equal(JsonValueKind.Null, duos.GetProperty("selectedPlayerEntry").ValueKind);
        Assert.Equal(JsonValueKind.Null, duos.GetProperty("selectedBandEntry").ValueKind);
    }

    [Fact]
    public async Task PrecomputeAllAsync_LeaderboardAll_prefers_current_state_snapshot_rows()
    {
        SeedSong("s1", "Solo_Guitar", 100000,
            ("live_only", 80000));
        InsertSnapshotEntry(77, "s1", "Solo_Guitar", "snap_top", 99000);
        InsertSnapshotState("s1", "Solo_Guitar", 77);

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var result = _sut.TryGet("lb:s1:10:");
        Assert.NotNull(result);

        var json = JsonDocument.Parse(result.Value.Json);
        var instrument = json.RootElement.GetProperty("instruments")
            .EnumerateArray()
            .Single(x => x.GetProperty("instrument").GetString() == "Solo_Guitar");
        var topEntry = instrument.GetProperty("entries")[0];

        Assert.Equal("snap_top", topEntry.GetProperty("accountId").GetString());
        Assert.Equal(99000, topEntry.GetProperty("score").GetInt32());
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
    public async Task PrecomputeAllAsync_PopulationTiers_UseCurrentStateThresholdBandRows()
    {
        SeedSong("s1", "Solo_Guitar", 100000,
            ("live_low", 90000), ("live_band", 97000));
        InsertSnapshotEntry(77, "s1", "Solo_Guitar", "snap_band", 99000);
        InsertSnapshotState("s1", "Solo_Guitar", 77);

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var tiers = _sut.GetPopulationTiers();
        Assert.NotNull(tiers);

        var tierData = tiers![("s1", "Solo_Guitar")];
        Assert.Equal(0, tierData.BaseCount);
        Assert.Single(tierData.Tiers);
        Assert.Equal(-1.0, tierData.Tiers[0].Leeway);
        Assert.Equal(1, tierData.Tiers[0].Total);
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
        var validScores = json.RootElement.GetProperty("scores")[0].GetProperty("vs");
        var fallback = validScores[0];

        // Should have rankTiers
        Assert.True(fallback.TryGetProperty("rt", out var rankTiers));
        Assert.True(rankTiers.GetArrayLength() > 0);

        // Each tier should have leeway and rank
        var firstTier = rankTiers[0];
        Assert.True(firstTier.TryGetProperty("l", out _));
        Assert.True(firstTier.TryGetProperty("r", out _));
    }

    [Fact]
    public async Task PrecomputeAllAsync_RankTiers_UseCurrentStateThresholdBandRows()
    {
        RegisterUser("user1");
        SeedSong("s1", "Solo_Guitar", 100000,
            ("user1", 106000),
            ("live_only_above", 103000),
            ("live_only_below", 90000));
        InsertSnapshotEntry(77, "s1", "Solo_Guitar", "user1", 106000);
        InsertSnapshotEntry(77, "s1", "Solo_Guitar", "snap_band", 101000);
        InsertSnapshotState("s1", "Solo_Guitar", 77);
        InsertScoreHistory("user1", "s1", "Solo_Guitar", 98000);

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var result = _sut.TryGet("player:user1:::");
        Assert.NotNull(result);

        var json = JsonDocument.Parse(result.Value.Json);
        var rankTiers = json.RootElement
            .GetProperty("scores")[0]
            .GetProperty("vs")[0]
            .GetProperty("rt");

        Assert.Equal(-5.0, rankTiers[0].GetProperty("l").GetDouble());
        Assert.Equal(1, rankTiers[0].GetProperty("r").GetInt32());
        Assert.Equal(1.0, rankTiers[1].GetProperty("l").GetDouble());
        Assert.Equal(2, rankTiers[1].GetProperty("r").GetInt32());
    }

    [Fact]
    public async Task PrecomputeAll_FlushesToPostgreSQLAndClearsRAM()
    {
        RegisterUser("user1");
        SeedSong("s1", "Solo_Guitar", 100000,
            ("user1", 95000), ("p2", 90000));

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        // _store should be cleared after flush
        Assert.Equal(0, _sut.Count);

        // But responses should be available via TryGet (reads from PostgreSQL cache)
        var response = _sut.TryGet("player:user1:::");
        Assert.NotNull(response);

        // Verify population tiers survived (stored separately, not in PostgreSQL)
        Assert.NotNull(_sut.GetPopulationTiers());
    }

    [Fact]
    public async Task PrecomputeAll_ClearsStalePostgreSQLDataBeforeFlush()
    {
        RegisterUser("user1");
        SeedSong("s1", "Solo_Guitar", 100000,
            ("user1", 95000));

        // First precomputation
        await _sut.PrecomputeAllAsync(CancellationToken.None);

        // Verify data is in cache
        Assert.NotNull(_sut.TryGet("player:user1:::"));

        // Second precomputation should TRUNCATE and re-insert
        await _sut.PrecomputeAllAsync(CancellationToken.None);
        Assert.NotNull(_sut.TryGet("player:user1:::"));
    }

    [Fact]
    public void GetCachedResponse_MissingKey_ReturnsNull()
    {
        // ClearCachedResponses should not throw even with empty table
        _metaDb.ClearCachedResponses();
        var response = _sut.TryGet("nonexistent:key");
        Assert.Null(response);
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
        var dsField = typeof(PathDataStore)
            .GetField("_ds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var ds = (Npgsql.NpgsqlDataSource)dsField.GetValue(_pathDataStore)!;
        using var conn = ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO songs (song_id) VALUES (@sid) ON CONFLICT DO NOTHING";
        cmd.Parameters.AddWithValue("sid", songId);
        cmd.ExecuteNonQuery();
    }
}
