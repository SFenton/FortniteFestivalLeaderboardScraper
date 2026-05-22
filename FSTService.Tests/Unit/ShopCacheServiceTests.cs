using System.Text.Json;
using System.Reflection;
using FortniteFestival.Core;
using FortniteFestival.Core.Services;
using FSTService.Api;
using Xunit;

namespace FSTService.Tests.Unit;

public sealed class ShopCacheServiceTests
{
    // ─── Get / Set / ETag ──────────────────────────────────

    [Fact]
    public void Get_ReturnsNull_WhenNothingCached()
    {
        var svc = new ShopCacheService();
        Assert.Null(svc.Get());
    }

    [Fact]
    public void Set_ThenGet_ReturnsCachedJson()
    {
        var svc = new ShopCacheService();
        var json = """{"count":0,"songs":[]}"""u8.ToArray();
        var etag = svc.Set(json);

        Assert.NotNull(etag);
        Assert.StartsWith("\"", etag);
        Assert.EndsWith("\"", etag);

        var result = svc.Get();
        Assert.NotNull(result);
        Assert.Equal(json, result!.Value.Json);
        Assert.Equal(etag, result.Value.ETag);
    }

    [Fact]
    public void Set_DifferentContent_ChangesETag()
    {
        var svc = new ShopCacheService();
        var etag1 = svc.Set("""{"count":1}"""u8.ToArray());
        var etag2 = svc.Set("""{"count":2}"""u8.ToArray());
        Assert.NotEqual(etag1, etag2);
    }

    [Fact]
    public void Set_SameContent_ProducesSameETag()
    {
        var svc = new ShopCacheService();
        var data = """{"count":1}"""u8.ToArray();
        var etag1 = svc.Set(data);
        var etag2 = svc.Set(data);
        Assert.Equal(etag1, etag2);
    }

    // ─── Prime ─────────────────────────────────────────────

    [Fact]
    public void Prime_WithEmptySongs_ProducesValidJson()
    {
        var svc = new ShopCacheService();
        var festivalService = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var inShop = new HashSet<string> { "song1" };
        var leaving = new HashSet<string>();
        var newSongs = new HashSet<string>();
        var opts = new JsonSerializerOptions();

        var bytes = svc.Prime(inShop, leaving, newSongs, festivalService, opts);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);

        var doc = JsonDocument.Parse(bytes);
        // Song not in catalog → 0 enriched songs
        Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
        Assert.NotNull(svc.Get());
    }

    [Fact]
    public void Prime_IncludesNewSongsAndIsNewFlag()
    {
        var svc = new ShopCacheService();
        var festivalService = CreateFestivalServiceWithSong("song1", "Song One");
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var bytes = svc.Prime(
            new HashSet<string> { "song1" },
            new HashSet<string>(),
            new HashSet<string> { "song1" },
            festivalService,
            opts);

        using var doc = JsonDocument.Parse(bytes);
        Assert.Equal("song1", doc.RootElement.GetProperty("newSongs")[0].GetString());
        Assert.True(doc.RootElement.GetProperty("songs")[0].GetProperty("isNew").GetBoolean());
    }

    // ─── BuildEnrichedSongList ─────────────────────────────

    [Fact]
    public void BuildEnrichedSongList_EmptyCatalog_ReturnsEmpty()
    {
        var festivalService = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var leaving = new HashSet<string>();
        var newSongs = new HashSet<string>();

        var result = ShopCacheService.BuildEnrichedSongList(
            new[] { "song1" }, leaving, newSongs, festivalService);

        Assert.Empty(result);
    }

    private static FestivalService CreateFestivalServiceWithSong(string songId, string title)
    {
        var festivalService = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var songsField = typeof(FestivalService).GetField("_songs", flags)!;
        var dirtyField = typeof(FestivalService).GetField("_songsDirty", flags)!;
        var songs = (Dictionary<string, Song>)songsField.GetValue(festivalService)!;
        songs[songId] = new Song
        {
            track = new Track { su = songId, tt = title, an = "Artist" }
        };
        dirtyField.SetValue(festivalService, true);
        return festivalService;
    }
}
