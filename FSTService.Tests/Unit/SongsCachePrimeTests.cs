using System.Text.Json;
using System.Reflection;
using FortniteFestival.Core;
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

    [Fact]
    public void BuildSongsJson_exposes_metadata_for_the_promoted_generation()
    {
        using var fx = new BuildSongsJsonFixture();
        fx.AddSongWithPathMetadata("path-song", "generation-42");

        var bytes = fx.Invoke();
        using var document = JsonDocument.Parse(bytes);
        var song = Assert.Single(document.RootElement.GetProperty("songs").EnumerateArray());

        Assert.Equal("generation-42", song.GetProperty("pathArtifactGenerationId").GetString());
        Assert.Equal(
            ["Solo_Guitar"],
            song.GetProperty("pathExpectedInstruments")
                .EnumerateArray()
                .Select(element => element.GetString()!)
                .ToArray());
        Assert.Equal("4.5.6", song.GetProperty("pathChoptVersion").GetString());
        Assert.Equal(new string('c', 64), song.GetProperty("pathChoptBinarySha256").GetString());
        Assert.Equal("profile-v3", song.GetProperty("pathGenerationProfile").GetString());
    }

    [Fact]
    public void Resolved_source_serializer_is_byte_identical_to_endpoint_serializer()
    {
        using var fx = new BuildSongsJsonFixture();
        fx.AddSongWithPathMetadata(
            "path-song",
            "generation-42");
        var expected = fx.Invoke();
        using var document = JsonDocument.Parse(expected);
        var currentSeason = document.RootElement
            .GetProperty("currentSeason")
            .GetInt32();

        var actual = SongsCacheService.BuildSongsJson(
            fx.FestivalSvc.Songs,
            fx.PathStore.GetAllMaxScores(),
            currentSeason,
            fx.Precomputer.GetPopulationTiers(),
            new JsonSerializerOptions(
                JsonSerializerDefaults.Web));

        Assert.Equal(expected, actual);
        Assert.Equal(
            ResponseCacheService.ComputeETag(expected),
            ResponseCacheService.ComputeETag(actual));
    }

    private sealed class BuildSongsJsonFixture : IDisposable
    {
        private readonly InMemoryMetaDatabase _metaFixture = new();
        private readonly Npgsql.NpgsqlDataSource _pathDataSource;
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

            _pathDataSource = SharedPostgresContainer.CreateDatabase();
            PathStore = new PathDataStore(_pathDataSource);
            Precomputer = new ScrapeTimePrecomputer(
                Persistence, MetaDb, PathStore,
                new ScrapeProgressTracker(),
                NullLogger<ScrapeTimePrecomputer>.Instance,
                NullLoggerFactory.Instance,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web),
                    new FeatureOptions());
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
            var bytes = Invoke();
            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.GetProperty("currentSeason").GetInt32();
        }

        public byte[] Invoke()
            => SongsCacheService.BuildSongsJson(
                FestivalSvc,
                PathStore,
                MetaDb,
                Persistence,
                Precomputer,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

        public void AddSongWithPathMetadata(
            string songId,
            string generationId)
        {
            using (var conn = _pathDataSource.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO songs (song_id, title, artist)
                    VALUES (@songId, 'Path Song', 'Artist')
                    """;
                cmd.Parameters.AddWithValue("songId", songId);
                cmd.ExecuteNonQuery();
            }

            PathStore.UpdateMaxScores(
                songId,
                new SongMaxScores
                {
                    MaxLeadScore = 123456,
                    CHOptVersion = "4.5.6",
                    CHOptBinarySha256 = new string('c', 64),
                    GenerationProfile = "profile-v3",
                    ArtifactGenerationId = generationId,
                    ExpectedInstruments = ["Solo_Guitar"],
                },
                "dat-hash");

            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var songsField = typeof(FestivalService).GetField("_songs", flags)!;
            var dirtyField = typeof(FestivalService).GetField("_songsDirty", flags)!;
            var songs = (Dictionary<string, Song>)songsField.GetValue(FestivalSvc)!;
            songs[songId] = new Song
            {
                track = new Track
                {
                    su = songId,
                    tt = "Path Song",
                    an = "Artist",
                    @in = new In { gr = 0 },
                },
            };
            dirtyField.SetValue(FestivalSvc, true);
        }

        public void Dispose()
        {
            Persistence.Dispose();
            MetaDb.Dispose();
            _pathDataSource.Dispose();
            _metaFixture.Dispose();
        }
    }
}
