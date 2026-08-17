using System.Text;
using FSTService.Api;
using FSTService.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class PublicationApiResponseCacheServiceTests
{
    [Fact]
    public void L2_hit_recovers_after_restart_then_uses_L1()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState()
            .Returns(PublicReadFreezeState.NotFrozen);
        var json = Encoding.UTF8.GetBytes("{\"ok\":true}");
        var cached = new PublicationCachedResponse(
            42,
            1302,
            DateTime.UtcNow,
            json,
            ResponseCacheService.ComputeETag(json),
            DateTime.UtcNow,
            "application/json",
            Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(json))
                .ToLowerInvariant());
        metaDb.GetCurrentCacheLookup("canonical")
            .Returns(new PublicationCacheLookup(true, cached));
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var service = new PublicationApiResponseCacheService(
            metaDb,
            gate,
            () => 42,
            NullLogger<PublicationApiResponseCacheService>.Instance);
        var plan = Plan("requested", "canonical");

        var first = service.TryGetCurrent(plan);
        var second = service.TryGetCurrent(plan);

        Assert.NotNull(first);
        Assert.Equal(PublicationApiCacheTier.L2, first.Value.Tier);
        Assert.NotNull(second);
        Assert.Equal(PublicationApiCacheTier.L1, second.Value.Tier);
        metaDb.Received(1).GetCurrentCacheLookup("canonical");
        Assert.Equal(json, second.Value.Json);
        Assert.Equal(cached.ETag, second.Value.ETag);
    }

    [Fact]
    public void Safety_revision_change_invalidates_L1()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState()
            .Returns(
                PublicReadFreezeState.NotFrozen,
                new PublicReadFreezeState(
                    true,
                    DateTime.UtcNow,
                    1302,
                    "maintenance"));
        var json = Encoding.UTF8.GetBytes("{\"revision\":1}");
        var cached = Cached(json);
        metaDb.GetCurrentCacheLookup("canonical")
            .Returns(new PublicationCacheLookup(true, cached));
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var service = new PublicationApiResponseCacheService(
            metaDb,
            gate,
            () => 42,
            NullLogger<PublicationApiResponseCacheService>.Instance);
        var plan = Plan("requested", "canonical");

        Assert.Equal(
            PublicationApiCacheTier.L2,
            service.TryGetCurrent(plan)!.Value.Tier);
        gate.Invalidate();
        Assert.Equal(
            PublicationApiCacheTier.L2,
            service.TryGetCurrent(plan)!.Value.Tier);

        metaDb.Received(2).GetCurrentCacheLookup("canonical");
    }

    [Fact]
    public void Publication_switch_never_reuses_previous_L1_entry()
    {
        var publicationId = 42L;
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState()
            .Returns(PublicReadFreezeState.NotFrozen);
        var oldJson = Encoding.UTF8.GetBytes(
            "{\"publication\":42}");
        var newJson = Encoding.UTF8.GetBytes(
            "{\"publication\":43}");
        metaDb.GetCurrentCacheLookup("canonical")
            .Returns(
                new PublicationCacheLookup(
                    true,
                    Cached(oldJson) with
                    {
                        PublicationId = 42,
                    }),
                new PublicationCacheLookup(
                    true,
                    Cached(newJson) with
                    {
                        PublicationId = 43,
                        PublishedScrapeId = 1303,
                    }));
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var service =
            new PublicationApiResponseCacheService(
                metaDb,
                gate,
                () => publicationId,
                NullLogger<
                    PublicationApiResponseCacheService>.Instance);
        var plan = Plan(
            "requested",
            "canonical");

        Assert.Equal(
            oldJson,
            service.TryGetCurrent(plan)!.Value.Json);
        Assert.Equal(
            PublicationApiCacheTier.L1,
            service.TryGetCurrent(plan)!.Value.Tier);

        publicationId = 43;

        var switched = service.TryGetCurrent(plan);
        Assert.NotNull(switched);
        Assert.Equal(43, switched.Value.PublicationId);
        Assert.Equal(newJson, switched.Value.Json);
        Assert.Equal(
            PublicationApiCacheTier.L2,
            switched.Value.Tier);
        metaDb.Received(2)
            .GetCurrentCacheLookup("canonical");
    }

    [Fact]
    public async Task Single_flight_serializes_same_publication_and_key()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState()
            .Returns(PublicReadFreezeState.NotFrozen);
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var service = new PublicationApiResponseCacheService(
            metaDb,
            gate,
            () => 42,
            NullLogger<PublicationApiResponseCacheService>.Instance);

        await using var first = await service.AcquireBuildLeaseAsync(
            42,
            "key",
            CancellationToken.None);
        var secondTask = service.AcquireBuildLeaseAsync(
                42,
                "key",
                CancellationToken.None)
            .AsTask();

        await Task.Delay(25);
        Assert.False(secondTask.IsCompleted);
        await first.DisposeAsync();
        await using var second = await secondTask;

        Assert.True(second.Waited);
    }

    [Fact]
    public void Frozen_store_is_blocked_without_database_write()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState()
            .Returns(new PublicReadFreezeState(
                true,
                DateTime.UtcNow,
                1302,
                "maintenance"));
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var service = new PublicationApiResponseCacheService(
            metaDb,
            gate,
            () => 42,
            NullLogger<PublicationApiResponseCacheService>.Instance);

        var stored = service.TryStoreCurrent(
            42,
            "key",
            Encoding.UTF8.GetBytes("{}"),
            "\"etag\"");

        Assert.Null(stored);
        metaDb.DidNotReceive().TrySetCurrentCachedResponse(
            Arg.Any<long>(),
            Arg.Any<string>(),
            Arg.Any<byte[]>(),
            Arg.Any<string>());
    }

    [Fact]
    public void Same_publication_write_replaces_L1_revision()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState()
            .Returns(PublicReadFreezeState.NotFrozen);
        var oldJson = Encoding.UTF8.GetBytes(
            "{\"revision\":1}");
        var newJson = Encoding.UTF8.GetBytes(
            "{\"revision\":2}");
        metaDb.GetCurrentCacheLookup("canonical")
            .Returns(
                new PublicationCacheLookup(
                    true,
                    Cached(oldJson)),
                new PublicationCacheLookup(
                    true,
                    Cached(newJson)));
        metaDb.TrySetCurrentCachedResponse(
                42,
                "canonical",
                newJson,
                ResponseCacheService.ComputeETag(newJson))
            .Returns(Cached(newJson));
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var service = new PublicationApiResponseCacheService(
            metaDb,
            gate,
            () => 42,
            NullLogger<PublicationApiResponseCacheService>.Instance);
        var plan = Plan("requested", "canonical");

        Assert.Equal(
            oldJson,
            service.TryGetCurrent(plan)!.Value.Json);
        Assert.NotNull(service.TryStoreCurrent(
            42,
            "canonical",
            newJson,
            ResponseCacheService.ComputeETag(newJson)));
        Assert.Equal(
            newJson,
            service.TryGetCurrent(plan)!.Value.Json);
    }

    [Fact]
    public void Previous_publication_lookup_uses_exact_L2_generation()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState()
            .Returns(PublicReadFreezeState.NotFrozen);
        var json = Encoding.UTF8.GetBytes(
            "{\"publication\":41}");
        var cached = Cached(json) with
        {
            PublicationId = 41,
            PublishedScrapeId = 1301,
        };
        metaDb.GetCachedResponseEntry(41, "canonical")
            .Returns(cached);
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var service = new PublicationApiResponseCacheService(
            metaDb,
            gate,
            () => 42,
            NullLogger<PublicationApiResponseCacheService>.Instance);

        var hit = service.TryGet(
            41,
            Plan("requested", "canonical"));

        Assert.NotNull(hit);
        Assert.Equal(41, hit.Value.PublicationId);
        Assert.Equal(json, hit.Value.Json);
        metaDb.Received(1).GetCachedResponseEntry(
            41,
            "canonical");
    }

    [Fact]
    public void Failed_write_through_returns_null_without_poisoning_L1()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState()
            .Returns(PublicReadFreezeState.NotFrozen);
        metaDb.TrySetCurrentCachedResponse(
                Arg.Any<long>(),
                Arg.Any<string>(),
                Arg.Any<byte[]>(),
                Arg.Any<string>())
            .Returns(_ => throw new InvalidOperationException(
                "read-only"));
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var service = new PublicationApiResponseCacheService(
            metaDb,
            gate,
            () => 42,
            NullLogger<PublicationApiResponseCacheService>.Instance);

        Assert.Null(service.TryStoreCurrent(
            42,
            "key",
            Encoding.UTF8.GetBytes("{}"),
            "\"etag\""));
        Assert.Null(service.TryGetCurrent(
            Plan("requested", "key")));
    }

    [Fact]
    public void Identical_current_row_avoids_redundant_write_through()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState()
            .Returns(PublicReadFreezeState.NotFrozen);
        var json = Encoding.UTF8.GetBytes(
            "{\"stable\":true}");
        var cached = Cached(json);
        metaDb.GetCurrentCacheLookup("key")
            .Returns(new PublicationCacheLookup(
                true,
                cached));
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var service = new PublicationApiResponseCacheService(
            metaDb,
            gate,
            () => 42,
            NullLogger<
                PublicationApiResponseCacheService>.Instance);

        var result = service.TryStoreCurrent(
            42,
            "key",
            json,
            cached.ETag);

        Assert.Same(cached, result);
        metaDb.DidNotReceive()
            .TrySetCurrentCachedResponse(
                Arg.Any<long>(),
                Arg.Any<string>(),
                Arg.Any<byte[]>(),
                Arg.Any<string>());
    }

    [Fact]
    public void Stale_same_publication_key_bypasses_L2_unfrozen_but_remains_freeze_safe()
    {
        var frozen = false;
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(_ =>
            frozen
                ? new PublicReadFreezeState(
                    true,
                    DateTime.UtcNow,
                    1302,
                    "scrape")
                : PublicReadFreezeState.NotFrozen);
        var json = Encoding.UTF8.GetBytes(
            "{\"revision\":\"old\"}");
        metaDb.GetCurrentCacheLookup(
                PublicationApiCacheKeys.Songs)
            .Returns(new PublicationCacheLookup(
                true,
                Cached(json)));
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var service = new PublicationApiResponseCacheService(
            metaDb,
            gate,
            () => 42,
            NullLogger<PublicationApiResponseCacheService>.Instance);
        var plan = Plan(
            "requested",
            PublicationApiCacheKeys.Songs);
        service.MarkCurrentKeyStale(
            PublicationApiCacheKeys.Songs);

        Assert.Null(service.TryGetCurrent(plan));

        frozen = true;
        gate.Invalidate();
        Assert.Equal(
            json,
            service.TryGetCurrent(plan)!.Value.Json);
    }

    [Fact]
    public void First_page_alias_projects_exact_bytes_and_etag()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState()
            .Returns(PublicReadFreezeState.NotFrozen);
        var source = Encoding.UTF8.GetBytes(
            "{\"page\":1,\"pageSize\":50,\"entries\":[1,2,3,4]}");
        metaDb.GetCurrentCacheLookup("canonical")
            .Returns(new PublicationCacheLookup(
                true,
                Cached(source)));
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var service = new PublicationApiResponseCacheService(
            metaDb,
            gate,
            () => 42,
            NullLogger<PublicationApiResponseCacheService>.Instance);
        var plan = new PublicApiCacheRequestPlan(
            "requested",
            [
                new PublicApiCacheLookupCandidate(
                    "canonical",
                    PublicApiCacheTransform.FirstPageSubset,
                    Page: 1,
                    PageSize: 2),
            ],
            FreezeCritical: true,
            AllowWriteThrough: false,
            TimeSpan.FromSeconds(1),
            2 * 1024 * 1024);

        var hit = service.TryGetCurrent(plan);

        Assert.NotNull(hit);
        Assert.Equal(
            "{\"page\":1,\"pageSize\":2,\"entries\":[1,2]}",
            Encoding.UTF8.GetString(hit.Value.Json));
        Assert.Equal(
            ResponseCacheService.ComputeETag(hit.Value.Json),
            hit.Value.ETag);
    }

    [Fact]
    public void Alias_projection_preserves_relaxed_unicode_bytes()
    {
        var firstPageSource = Encoding.UTF8.GetBytes(
            "{\"page\":1,\"pageSize\":50,\"entries\":["
            + "{\"displayName\":\"Jöhn\"},"
            + "{\"displayName\":\"Łukasz\"}]}");
        var firstPageExpected = Encoding.UTF8.GetBytes(
            "{\"page\":1,\"pageSize\":1,\"entries\":["
            + "{\"displayName\":\"Jöhn\"}]}");
        var overviewSource = Encoding.UTF8.GetBytes(
            "{\"rankBy\":\"adjusted\",\"pageSize\":10,"
            + "\"instruments\":{\"Solo_Guitar\":{"
            + "\"totalAccounts\":2,\"entries\":["
            + "{\"displayName\":\"Jöhn\"},"
            + "{\"displayName\":\"Łukasz\"}]}}}");
        var overviewExpected = Encoding.UTF8.GetBytes(
            "{\"rankBy\":\"adjusted\",\"pageSize\":1,"
            + "\"instruments\":{\"Solo_Guitar\":{"
            + "\"totalAccounts\":2,\"entries\":["
            + "{\"displayName\":\"Jöhn\"}]}}}");

        var firstPage =
            CacheHelper.ProjectFirstPageSubset(
                firstPageSource,
                requestedPage: 1,
                requestedPageSize: 1);
        var overview =
            CacheHelper.ProjectOverviewSubset(
                overviewSource,
                requestedPageSize: 1);

        Assert.Equal(
            firstPageExpected,
            firstPage);
        Assert.Equal(
            overviewExpected,
            overview);
        Assert.DoesNotContain(
            "\\u",
            Encoding.UTF8.GetString(firstPage!),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "\\u",
            Encoding.UTF8.GetString(overview!),
            StringComparison.OrdinalIgnoreCase);
    }

    private static PublicationCachedResponse Cached(byte[] json) => new(
        42,
        1302,
        DateTime.UtcNow,
        json,
        ResponseCacheService.ComputeETag(json),
        DateTime.UtcNow,
        "application/json",
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(json))
            .ToLowerInvariant());

    private static PublicApiCacheRequestPlan Plan(
        string requested,
        string canonical) => new(
        requested,
        [new PublicApiCacheLookupCandidate(canonical)],
        FreezeCritical: true,
        AllowWriteThrough: false,
        TimeSpan.FromSeconds(1),
        2 * 1024 * 1024);
}
