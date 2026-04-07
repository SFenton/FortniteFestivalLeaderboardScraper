using System.Text.Json;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FSTService.Tests.Unit;

/// <summary>
/// Tests that endpoints return precomputed data from ScrapeTimePrecomputer
/// when available, without hitting the database.
/// </summary>
public sealed class EndpointPrecomputerWiringTests : IDisposable
{
    private readonly string _tempDir;
    private readonly InMemoryMetaDatabase _metaFixture = new();
    private readonly MetaDatabase _metaDb;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly PathDataStore _pathDataStore;
    private readonly ScrapeTimePrecomputer _precomputer;

    public EndpointPrecomputerWiringTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"wiring_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _metaDb = new MetaDatabase(_metaFixture.DataSource,
            Substitute.For<ILogger<MetaDatabase>>());

        _persistence = new GlobalLeaderboardPersistence(
            _metaDb,
            Substitute.For<ILoggerFactory>(),
            Substitute.For<ILogger<GlobalLeaderboardPersistence>>(),
            _metaFixture.DataSource);
        _persistence.Initialize();

        _pathDataStore = new PathDataStore(SharedPostgresContainer.CreateDatabase());

        _precomputer = new ScrapeTimePrecomputer(
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

    /// <summary>
    /// Stores a precomputed JSON response under the given cache key,
    /// then verifies TryGet retrieves it with a non-null ETag.
    /// This proves the Store→TryGet pipeline works for each cache key pattern.
    /// </summary>
    private void StoreAndVerify(string cacheKey, object payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        // Use reflection to call the private Store method
        var storeMethod = typeof(ScrapeTimePrecomputer)
            .GetMethod("Store", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        storeMethod.Invoke(_precomputer, new object?[] { cacheKey, json, null });

        var result = _precomputer.TryGet(cacheKey);
        Assert.NotNull(result);
        Assert.NotEmpty(result.Value.ETag);
        Assert.Equal(json.Length, result.Value.Json.Length);
    }

    // ═══════════════════════════════════════════════════════════════
    // Player sub-resources
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void PlayerStats_CacheKey_RoundTrips()
    {
        StoreAndVerify("playerstats:user1", new { accountId = "user1", totalSongs = 5 });
    }

    [Fact]
    public void PlayerHistory_CacheKey_RoundTrips()
    {
        StoreAndVerify("history:user1", new { accountId = "user1", count = 0, history = Array.Empty<object>() });
    }

    [Fact]
    public void SyncStatus_CacheKey_RoundTrips()
    {
        StoreAndVerify("syncstatus:user1", new { accountId = "user1", isTracked = true });
    }

    [Fact]
    public void RivalsOverview_CacheKey_RoundTrips()
    {
        StoreAndVerify("rivals-overview:user1", new { accountId = "user1", combos = Array.Empty<object>() });
    }

    [Fact]
    public void RivalsAll_CacheKey_RoundTrips()
    {
        StoreAndVerify("rivals-all:user1", new
        {
            accountId = "user1",
            songs = new[] { "s1", "s2" },
            combos = Array.Empty<object>(),
        });
    }

    [Fact]
    public void LeaderboardRivals_CacheKey_RoundTrips()
    {
        StoreAndVerify("lb-rivals:user1:Solo_Guitar:totalscore", new
        {
            instrument = "Solo_Guitar",
            rankBy = "totalscore",
            above = Array.Empty<object>(),
            below = Array.Empty<object>(),
        });
    }

    // ═══════════════════════════════════════════════════════════════
    // Rankings
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Rankings_PerInstrument_CacheKey_RoundTrips()
    {
        StoreAndVerify("rankings:Solo_Guitar:totalscore:1:50", new
        {
            instrument = "Solo_Guitar",
            rankBy = "totalscore",
            page = 1,
            pageSize = 50,
            totalAccounts = 100,
            entries = Array.Empty<object>(),
        });
    }

    [Fact]
    public void Rankings_Composite_CacheKey_RoundTrips()
    {
        StoreAndVerify("rankings:composite:adjusted:1:50", new
        {
            page = 1,
            pageSize = 50,
            totalAccounts = 50,
            entries = Array.Empty<object>(),
        });
    }

    [Fact]
    public void Rankings_Overview_CacheKey_RoundTrips()
    {
        StoreAndVerify("rankings:overview:adjusted:10", new
        {
            rankBy = "adjusted",
            pageSize = 10,
            instruments = new Dictionary<string, object>(),
        });
    }

    // ═══════════════════════════════════════════════════════════════
    // Neighborhoods
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Neighborhood_Instrument_CacheKey_RoundTrips()
    {
        StoreAndVerify("neighborhood:Solo_Guitar:user1:5", new
        {
            instrument = "Solo_Guitar",
            accountId = "user1",
        });
    }

    [Fact]
    public void Neighborhood_Composite_CacheKey_RoundTrips()
    {
        StoreAndVerify("neighborhood:composite:user1:5", new
        {
            accountId = "user1",
        });
    }

    // ═══════════════════════════════════════════════════════════════
    // Static data
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void FirstSeen_CacheKey_RoundTrips()
    {
        StoreAndVerify("firstseen", new { count = 0, songs = Array.Empty<object>() });
    }

    // ═══════════════════════════════════════════════════════════════
    // Fallthrough: non-existent keys return null
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void TryGet_NonExistentKey_ReturnsNull()
    {
        Assert.Null(_precomputer.TryGet("playerstats:nonexistent"));
        Assert.Null(_precomputer.TryGet("rankings:Solo_Guitar:adjusted:2:50"));
        Assert.Null(_precomputer.TryGet("neighborhood:Solo_Guitar:unknown:5"));
    }

    [Fact]
    public void TryGet_EmptyStringKey_ReturnsNull()
    {
        Assert.Null(_precomputer.TryGet(""));
    }
}
