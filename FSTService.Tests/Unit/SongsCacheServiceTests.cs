using FSTService.Api;
using FortniteFestival.Core;
using FSTService.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FSTService.Tests.Unit;

public class SongsCacheServiceTests
{
    [Fact]
    public void Public_max_scores_omit_missing_instrument_values()
    {
        var result = SongsCacheService.BuildPublicMaxScores(
            new SongMaxScores
            {
                MaxProLeadScore = 209_100,
            });

        Assert.NotNull(result);
        Assert.Equal(
            new Dictionary<string, int>
            {
                ["Solo_PeripheralGuitar"] = 209_100,
            },
            result);
    }

    [Fact]
    public void Public_max_scores_include_both_plastic_drum_modes()
    {
        var result = SongsCacheService.BuildPublicMaxScores(
            new SongMaxScores
            {
                MaxProCymbalsScore = 130_000,
                MaxProDrumsScore = 125_000,
            });

        Assert.NotNull(result);
        Assert.Equal(130_000, result["Solo_PeripheralCymbals"]);
        Assert.Equal(125_000, result["Solo_PeripheralDrums"]);
    }

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
    public void Stable_songs_write_is_persisted_to_current_publication()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState()
            .Returns(PublicReadFreezeState.NotFrozen);
        var json = System.Text.Encoding.UTF8.GetBytes(
            "{\"songs\":[]}");
        var etag = ResponseCacheService.ComputeETag(json);
        metaDb.TrySetCurrentCachedResponse(
                42,
                PublicationApiCacheKeys.Songs,
                json,
                etag)
            .Returns(new PublicationCachedResponse(
                42,
                1302,
                DateTime.UtcNow,
                json,
                etag));
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var publicationCache =
            new PublicationApiResponseCacheService(
                metaDb,
                gate,
                () => 42,
                NullLogger<
                    PublicationApiResponseCacheService>.Instance);
        var cache = new SongsCacheService(
            gate.GetCacheSafetySnapshot,
            TimeSpan.FromMinutes(5),
            () => 42,
            publicationCache);
        var token = cache.CaptureBuildToken();

        var result = cache.TrySetIfBuildTokenUnchanged(
            json,
            token,
            out var actualEtag,
            persistPublicationCache: true);

        Assert.Equal(SongsCacheWriteResult.Stored, result);
        Assert.Equal(etag, actualEtag);
        Assert.False(cache.DurableRefreshPending);
        metaDb.Received(1).TrySetCurrentCachedResponse(
            42,
            PublicationApiCacheKeys.Songs,
            json,
            etag);
    }

    [Fact]
    public void Failed_durable_songs_write_hides_prior_L2_until_recovery()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState()
            .Returns(PublicReadFreezeState.NotFrozen);
        var oldJson = System.Text.Encoding.UTF8.GetBytes(
            "{\"revision\":\"old\"}");
        metaDb.GetCurrentCacheLookup(
                PublicationApiCacheKeys.Songs)
            .Returns(new PublicationCacheLookup(
                true,
                new PublicationCachedResponse(
                    42,
                    1302,
                    DateTime.UtcNow,
                    oldJson,
                    ResponseCacheService.ComputeETag(
                        oldJson))));
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var publicationCache =
            new PublicationApiResponseCacheService(
                metaDb,
                gate,
                () => 42,
                NullLogger<
                    PublicationApiResponseCacheService>.Instance);
        var cache = new SongsCacheService(
            gate.GetCacheSafetySnapshot,
            TimeSpan.FromMinutes(5),
            () => 42,
            publicationCache);
        var newJson = System.Text.Encoding.UTF8.GetBytes(
            "{\"revision\":\"new\"}");

        Assert.Equal(
            SongsCacheWriteResult.DurableStoreFailed,
            cache.TrySetIfBuildTokenUnchanged(
                newJson,
                cache.CaptureBuildToken(),
                out _,
                persistPublicationCache: true));
        Assert.True(cache.DurableRefreshPending);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/songs";
        Assert.True(
            PublicApiResponseCachePolicy
                .TryCreateRequestPlan(
                    context.Request,
                    out var plan));
        Assert.Null(
            publicationCache.TryGetCurrent(plan));
    }

    [Fact]
    public async Task Content_mutation_racing_durable_store_keeps_L2_stale()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState()
            .Returns(PublicReadFreezeState.NotFrozen);
        var json = System.Text.Encoding.UTF8.GetBytes(
            "{\"revision\":\"old\"}");
        var storeStarted = new ManualResetEventSlim();
        var releaseStore = new ManualResetEventSlim();
        metaDb.TrySetCurrentCachedResponse(
                42,
                PublicationApiCacheKeys.Songs,
                json,
                Arg.Any<string>())
            .Returns(_ =>
            {
                storeStarted.Set();
                releaseStore.Wait();
                return new PublicationCachedResponse(
                    42,
                    1302,
                    DateTime.UtcNow,
                    json,
                    ResponseCacheService.ComputeETag(
                        json));
            });
        metaDb.GetCurrentCacheLookup(
                PublicationApiCacheKeys.Songs)
            .Returns(
                new PublicationCacheLookup(
                    true,
                    null),
                new PublicationCacheLookup(
                    true,
                    new PublicationCachedResponse(
                        42,
                        1302,
                        DateTime.UtcNow,
                        json,
                        ResponseCacheService.ComputeETag(
                            json))));
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var publicationCache =
            new PublicationApiResponseCacheService(
                metaDb,
                gate,
                () => 42,
                NullLogger<
                    PublicationApiResponseCacheService>.Instance);
        var cache = new SongsCacheService(
            gate.GetCacheSafetySnapshot,
            TimeSpan.FromMinutes(5),
            () => 42,
            publicationCache);
        var token = cache.CaptureBuildToken();
        var storeTask = Task.Run(() =>
            cache.TrySetIfBuildTokenUnchanged(
                json,
                token,
                out _,
                persistPublicationCache: true));
        Assert.True(
            storeStarted.Wait(TimeSpan.FromSeconds(5)));

        using var mutation = cache.BeginContentMutation();
        releaseStore.Set();

        Assert.Equal(
            SongsCacheWriteResult.Stale,
            await storeTask);
        Assert.True(cache.DurableRefreshPending);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/songs";
        Assert.True(
            PublicApiResponseCachePolicy
                .TryCreateRequestPlan(
                    context.Request,
                    out var plan));
        Assert.Null(
            publicationCache.TryGetCurrent(plan));
    }

    [Fact]
    public void Content_mutation_completion_triggers_one_durable_refresh()
    {
        var cache = new SongsCacheService();
        var refreshes = 0;
        cache.SetDurableRefresh(
            () => Interlocked.Increment(ref refreshes));

        using (cache.BeginContentMutation())
        {
            using (cache.BeginContentMutation())
            {
                Assert.Equal(0, refreshes);
            }
            Assert.Equal(0, refreshes);
        }

        Assert.Equal(1, refreshes);
        Assert.True(cache.DurableRefreshPending);
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
    public void Completed_safety_transition_discards_pre_freeze_entry()
    {
        var safety = new PublicReadCacheSafetySnapshot(
            IsFrozen: false,
            FailedCandidateIsolationActive: false,
            Revision: 1);
        var cache = new SongsCacheService(
            () => safety,
            TimeSpan.FromMinutes(5));
        cache.Set(System.Text.Encoding.UTF8.GetBytes("pre-freeze"));

        safety = safety with { IsFrozen = true, Revision = 2 };
        Assert.NotNull(cache.Get());

        safety = safety with { IsFrozen = false, Revision = 3 };
        Assert.Null(cache.Get());
        Assert.Null(cache.GetStale());
    }

    [Fact]
    public void Published_fallback_uses_richer_live_metadata_only_for_exact_identity()
    {
        var timestamp = new DateTime(
            2026,
            8,
            1,
            12,
            0,
            0,
            DateTimeKind.Utc);
        var published = new Song
        {
            lastModified = timestamp,
            track = new Track
            {
                su = "song-1",
                tt = "Song",
            },
        };
        var live = new Song
        {
            lastModified = timestamp,
            track = new Track
            {
                su = "song-1",
                tt = "Song",
                au = "https://cdn.example/song.jpg",
            },
        };

        var selected = SongsCacheService.SelectPublishedFallbackSongs(
            [published],
            [live]);

        Assert.Same(live, Assert.Single(selected));

        live.lastModified = timestamp.AddSeconds(1);
        selected = SongsCacheService.SelectPublishedFallbackSongs(
            [published],
            [live]);
        Assert.Same(published, Assert.Single(selected));
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
