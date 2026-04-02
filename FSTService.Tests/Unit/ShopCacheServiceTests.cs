using System.Text.Json;
using FortniteFestival.Core;
using FortniteFestival.Core.Services;
using FSTService.Api;
using FSTService.Scraping;
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
        var opts = new JsonSerializerOptions();

        var bytes = svc.Prime(inShop, leaving, festivalService, opts);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);

        var doc = JsonDocument.Parse(bytes);
        // Song not in catalog → 0 enriched songs
        Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
        Assert.NotNull(svc.Get());
    }

    // ─── BuildEnrichedSongList ─────────────────────────────

    [Fact]
    public void BuildEnrichedSongList_EmptyCatalog_ReturnsEmpty()
    {
        var festivalService = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var leaving = new HashSet<string>();

        var result = ShopCacheService.BuildEnrichedSongList(
            new[] { "song1" }, leaving, festivalService);

        Assert.Empty(result);
    }
}
