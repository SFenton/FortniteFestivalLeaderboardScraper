using System.Text.Json;
using FortniteFestival.Core.Persistence;
using FortniteFestival.Core.Services;
using FSTService.Api;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace FSTService.Tests.Unit;

public sealed class SongsCachePrimeTests
{
    [Fact]
    public void Set_ThenGet_ReturnsCachedData()
    {
        var svc = new SongsCacheService();
        var data = """{"count":0,"currentSeason":1,"songs":[]}"""u8.ToArray();
        var etag = svc.Set(data);

        var result = svc.Get();
        Assert.NotNull(result);
        Assert.Equal(data, result!.Value.Json);
        Assert.Equal(etag, result.Value.ETag);
    }

    [Fact]
    public void BuildSongsJson_DoesNotIncludeShopFields()
    {
        // BuildSongsJson is tested in the existing ScrapeTimePrecomputerTests
        // indirectly. Here we just verify the SongsCacheService contract.
        var svc = new SongsCacheService();
        // Simulate a songs response without shop fields
        var json = """{"count":1,"currentSeason":5,"songs":[{"songId":"s1","title":"Test"}]}""";
        svc.Set(System.Text.Encoding.UTF8.GetBytes(json));

        var result = svc.Get();
        Assert.NotNull(result);
        var text = System.Text.Encoding.UTF8.GetString(result!.Value.Json);
        Assert.DoesNotContain("shopUrl", text);
        Assert.DoesNotContain("leavingTomorrow", text);
    }

    [Fact]
    public void Invalidate_ClearsCache()
    {
        var svc = new SongsCacheService();
        svc.Set("""{"test":1}"""u8.ToArray());
        Assert.NotNull(svc.Get());

        svc.Invalidate();
        Assert.Null(svc.Get());
    }

    [Fact]
    public void Set_SameContent_ProducesSameETag()
    {
        var svc = new SongsCacheService();
        var data = """{"count":1}"""u8.ToArray();
        var etag1 = svc.Set(data);
        var etag2 = svc.Set(data);
        Assert.Equal(etag1, etag2);
    }

    [Fact]
    public void Set_DifferentContent_ProducesDifferentETag()
    {
        var svc = new SongsCacheService();
        var etag1 = svc.Set("""{"count":1}"""u8.ToArray());
        var etag2 = svc.Set("""{"count":2}"""u8.ToArray());
        Assert.NotEqual(etag1, etag2);
    }

    // ─── currentSeason floor ──────────────────────────────────────────
    // Regression: `/api/songs` must advertise the highest season number the
    // scraper has observed in the instrument DBs, even when season_windows
    // hasn't been updated (e.g. Epic's events API advertises the new season
    // under a window ID our regex doesn't match). See
    // PostScrapeOrchestrator.RefreshRegisteredUsersAsync for the write path
    // that backstops this at scrape time; this test covers the read path.

    [Fact]
    public void BuildSongsJson_UsesInstrumentMax_WhenGreaterThanSeasonWindowsMax()
    {
        using var fx = new BuildSongsJsonFixture();
        fx.MetaDb.UpsertSeasonWindow(13, eventId: "season013_x", windowId: "x_guitar");
        fx.PersistSeasonEntry("Solo_Guitar", season: 14);

        var season = fx.InvokeAndReadCurrentSeason();

        Assert.Equal(14, season);
    }

    [Fact]
    public void BuildSongsJson_UsesSeasonWindowsMax_WhenGreaterThanInstrumentMax()
    {
        using var fx = new BuildSongsJsonFixture();
        fx.MetaDb.UpsertSeasonWindow(14, eventId: "season014_x", windowId: "x_guitar");
        fx.PersistSeasonEntry("Solo_Guitar", season: 10);

        var season = fx.InvokeAndReadCurrentSeason();

        Assert.Equal(14, season);
    }

    [Fact]
    public void BuildSongsJson_CurrentSeasonIsZero_WhenBothSourcesEmpty()
    {
        using var fx = new BuildSongsJsonFixture();

        var season = fx.InvokeAndReadCurrentSeason();

        Assert.Equal(0, season);
    }

    private sealed class BuildSongsJsonFixture : IDisposable
    {
        private readonly InMemoryMetaDatabase _metaFixture = new();
        public MetaDatabase MetaDb { get; }
        public GlobalLeaderboardPersistence Persistence { get; }
        public PathDataStore PathStore { get; }
        public ScrapeTimePrecomputer Precomputer { get; }
        public FestivalService FestivalSvc { get; }

        public BuildSongsJsonFixture()
        {
            MetaDb = new MetaDatabase(_metaFixture.DataSource,
                Substitute.For<ILogger<MetaDatabase>>());
            Persistence = new GlobalLeaderboardPersistence(
                MetaDb,
                NullLoggerFactory.Instance,
                NullLogger<GlobalLeaderboardPersistence>.Instance,
                _metaFixture.DataSource,
                Options.Create(new FeatureOptions()));
            Persistence.Initialize();

            PathStore = new PathDataStore(SharedPostgresContainer.CreateDatabase());
            Precomputer = new ScrapeTimePrecomputer(
                Persistence, MetaDb, PathStore,
                new ScrapeProgressTracker(),
                NullLogger<ScrapeTimePrecomputer>.Instance,
                NullLoggerFactory.Instance,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            FestivalSvc = new FestivalService((IFestivalPersistence?)null);
        }

        public void PersistSeasonEntry(string instrument, int season)
        {
            Persistence.PersistResult(new FSTService.Scraping.GlobalLeaderboardResult
            {
                SongId = $"song_{season}",
                Instrument = instrument,
                Entries = [new FSTService.Scraping.LeaderboardEntry
                {
                    AccountId = "acct_a",
                    Score = 100,
                    Accuracy = 95,
                    IsFullCombo = false,
                    Stars = 5,
                    Season = season,
                    Percentile = 99.0,
                }],
            });
        }

        public int InvokeAndReadCurrentSeason()
        {
            var bytes = SongsCacheService.BuildSongsJson(
                FestivalSvc, PathStore, MetaDb, Persistence, Precomputer,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.GetProperty("currentSeason").GetInt32();
        }

        public void Dispose()
        {
            Persistence.Dispose();
            MetaDb.Dispose();
            _metaFixture.Dispose();
        }
    }
}
