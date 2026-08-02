using FSTService.Api;
using FortniteFestival.Core;

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

    [Fact]
    public void Get_ExpiredButFrozen_ReturnsStaleData()
    {
        var frozen = false;
        var cache = new SongsCacheService(() => frozen, TimeSpan.Zero);
        var data = System.Text.Encoding.UTF8.GetBytes("stale");
        var etag = cache.Set(data);

        frozen = true;
        var cached = cache.Get();

        Assert.NotNull(cached);
        Assert.Equal(data, cached!.Value.Json);
        Assert.Equal(etag, cached.Value.ETag);
    }

    [Fact]
    public void Get_ExpiredAndNotFrozen_ReturnsNull()
    {
        var cache = new SongsCacheService(() => false, TimeSpan.Zero);
        cache.Set(System.Text.Encoding.UTF8.GetBytes("stale"));

        Assert.Null(cache.Get());
    }

    [Fact]
    public void PublicationChange_DiscardsCachedSongs()
    {
        long? publicationId = 1;
        var cache = new SongsCacheService(
            static () => false,
            TimeSpan.FromMinutes(5),
            static () => false,
            () => publicationId);
        cache.Set(System.Text.Encoding.UTF8.GetBytes("publication-1"));
        Assert.NotNull(cache.Get());

        publicationId = 2;

        Assert.Null(cache.Get());
    }

    [Fact]
    public void GetStale_ExpiredAndNotFrozen_ReturnsCachedData()
    {
        var cache = new SongsCacheService(() => false, TimeSpan.Zero);
        var data = System.Text.Encoding.UTF8.GetBytes("stale");
        var etag = cache.Set(data);

        Assert.Null(cache.Get());
        var stale = cache.GetStale();

        Assert.NotNull(stale);
        Assert.Equal(data, stale!.Value.Json);
        Assert.Equal(etag, stale.Value.ETag);
    }

    [Fact]
    public void FailedCandidateIsolation_ClearsAndRejectsCacheWrites()
    {
        var failedCandidateIsolation = false;
        var cache = new SongsCacheService(
            static () => false,
            TimeSpan.FromMinutes(5),
            () => failedCandidateIsolation);
        cache.Set(System.Text.Encoding.UTF8.GetBytes("published"));
        Assert.NotNull(cache.Get());

        failedCandidateIsolation = true;

        Assert.Null(cache.Get());
        cache.Set(System.Text.Encoding.UTF8.GetBytes("candidate"));

        failedCandidateIsolation = false;

        Assert.Null(cache.Get());
    }

    [Fact]
    public void Stale_builder_cannot_install_after_invalidation()
    {
        var cache = new SongsCacheService();
        var token = cache.CaptureBuildToken();

        cache.Invalidate();

        Assert.Equal(
            SongsCacheWriteResult.Stale,
            cache.TrySetIfBuildTokenUnchanged(
            System.Text.Encoding.UTF8.GetBytes("stale-generation"),
            token,
            out _));
        Assert.Null(cache.Get());

        var currentToken = cache.CaptureBuildToken();
        Assert.Equal(
            SongsCacheWriteResult.Stored,
            cache.TrySetIfBuildTokenUnchanged(
            System.Text.Encoding.UTF8.GetBytes("current-generation"),
            currentToken,
            out _));
        Assert.Equal(
            "current-generation",
            System.Text.Encoding.UTF8.GetString(cache.Get()!.Value.Json));
    }

    [Fact]
    public void Safety_transition_blocks_builder_even_after_unfreeze()
    {
        var safety = new PublicReadCacheSafetySnapshot(
            IsFrozen: false,
            FailedCandidateIsolationActive: false,
            Revision: 1);
        var cache = new SongsCacheService(
            () => safety,
            TimeSpan.FromMinutes(5));
        var token = cache.CaptureBuildToken();

        safety = safety with { IsFrozen = true, Revision = 2 };
        safety = safety with { IsFrozen = false, Revision = 3 };

        Assert.Equal(
            SongsCacheWriteResult.Stale,
            cache.TrySetIfBuildTokenUnchanged(
                System.Text.Encoding.UTF8.GetBytes("crossed-freeze"),
                token,
                out _));
        Assert.Null(cache.Get());
    }

    [Fact]
    public void Active_failed_candidate_isolation_blocks_cache_install()
    {
        var safety = new PublicReadCacheSafetySnapshot(
            IsFrozen: false,
            FailedCandidateIsolationActive: false,
            Revision: 1);
        var cache = new SongsCacheService(
            () => safety,
            TimeSpan.FromMinutes(5));
        var token = cache.CaptureBuildToken();
        safety = safety with
        {
            FailedCandidateIsolationActive = true,
            Revision = 2,
        };

        Assert.Equal(
            SongsCacheWriteResult.Stale,
            cache.TrySetIfBuildTokenUnchanged(
                System.Text.Encoding.UTF8.GetBytes("isolated"),
                token,
                out _));
        var isolatedToken = cache.CaptureBuildToken();
        Assert.Equal(
            SongsCacheWriteResult.Blocked,
            cache.TrySetIfBuildTokenUnchanged(
                System.Text.Encoding.UTF8.GetBytes("isolated"),
                isolatedToken,
                out _));
        Assert.Null(cache.Get());
    }

    [Fact]
    public void Cache_read_uses_one_coherent_safety_snapshot()
    {
        var calls = 0;
        var cache = new SongsCacheService(
            () =>
            {
                calls++;
                return new PublicReadCacheSafetySnapshot(
                    IsFrozen: false,
                    FailedCandidateIsolationActive: false,
                    Revision: 1);
            },
            TimeSpan.FromMinutes(5));
        cache.Set(System.Text.Encoding.UTF8.GetBytes("published"));
        calls = 0;

        Assert.NotNull(cache.Get());
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Content_mutation_blocks_spanning_cache_installs()
    {
        var cache = new SongsCacheService();
        cache.Set(System.Text.Encoding.UTF8.GetBytes("old-generation"));
        var beforeMutation = cache.CaptureBuildToken();

        using (cache.BeginContentMutation())
        {
            Assert.Null(cache.Get());
            var duringMutation = cache.CaptureBuildToken();
            Assert.Equal(
                SongsCacheWriteResult.Blocked,
                cache.TrySetIfBuildTokenUnchanged(
                    System.Text.Encoding.UTF8.GetBytes("during-mutation"),
                    duringMutation,
                    out _));
        }

        Assert.Equal(
            SongsCacheWriteResult.Stale,
            cache.TrySetIfBuildTokenUnchanged(
                System.Text.Encoding.UTF8.GetBytes("stale-generation"),
                beforeMutation,
                out _));
        var current = cache.CaptureBuildToken();
        Assert.Equal(
            SongsCacheWriteResult.Stored,
            cache.TrySetIfBuildTokenUnchanged(
                System.Text.Encoding.UTF8.GetBytes("new-generation"),
                current,
                out _));
    }

    [Fact]
    public void OrderSongsForPublicResponse_IsStableAcrossSourceOrder()
    {
        var first = new Song { track = new Track { su = "song-b" } };
        var second = new Song { track = new Track { su = "song-a" } };

        var forward = SongsCacheService.OrderSongsForPublicResponse([first, second])
            .Select(song => song.track.su)
            .ToArray();
        var reverse = SongsCacheService.OrderSongsForPublicResponse([second, first])
            .Select(song => song.track.su)
            .ToArray();

        Assert.Equal(["song-a", "song-b"], forward);
        Assert.Equal(forward, reverse);
    }
}
