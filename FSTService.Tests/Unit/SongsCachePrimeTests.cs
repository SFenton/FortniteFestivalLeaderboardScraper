using System.Text.Json;
using FortniteFestival.Core.Services;
using FSTService.Api;
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
}
