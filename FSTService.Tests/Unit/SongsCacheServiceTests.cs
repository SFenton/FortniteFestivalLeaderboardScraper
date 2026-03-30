using FSTService.Api;

namespace FSTService.Tests.Unit;

public class SongsCacheServiceTests
{
    [Fact]
    public void Get_Empty_ReturnsNull()
    {
        var cache = new SongsCacheService();
        Assert.Null(cache.Get());
    }

    [Fact]
    public void Set_ThenGet_ReturnsCachedData()
    {
        var cache = new SongsCacheService();
        var data = System.Text.Encoding.UTF8.GetBytes("""{"songs":[]}""");
        var etag = cache.Set(data);

        Assert.NotNull(etag);

        var cached = cache.Get();
        Assert.NotNull(cached);
        Assert.Equal(data, cached!.Value.Json);
        Assert.Equal(etag, cached.Value.ETag);
    }

    [Fact]
    public void Invalidate_ClearsCache()
    {
        var cache = new SongsCacheService();
        cache.Set(System.Text.Encoding.UTF8.GetBytes("{}"));

        cache.Invalidate();

        Assert.Null(cache.Get());
    }

    [Fact]
    public void Set_MultipleTimes_OverwritesPrevious()
    {
        var cache = new SongsCacheService();
        cache.Set(System.Text.Encoding.UTF8.GetBytes("old"));
        var etag2 = cache.Set(System.Text.Encoding.UTF8.GetBytes("new"));

        var cached = cache.Get();
        Assert.NotNull(cached);
        Assert.Equal("new", System.Text.Encoding.UTF8.GetString(cached!.Value.Json));
        Assert.Equal(etag2, cached.Value.ETag);
    }
}
