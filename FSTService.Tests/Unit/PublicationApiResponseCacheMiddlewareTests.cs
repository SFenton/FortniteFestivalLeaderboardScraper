using System.Text;
using FSTService.Api;
using FSTService.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class PublicationApiResponseCacheMiddlewareTests
{
    [Fact]
    public async Task Frozen_songs_hit_serves_L2_without_build()
    {
        var json = Encoding.UTF8.GetBytes("{\"songs\":[1]}");
        var fixture = Fixture(
            new PublicReadFreezeState(
                true,
                DateTime.UtcNow,
                1302,
                PublicReadFreezeState
                    .MaxScoreMaintenanceReasonPrefix
                + new string('a', 64)));
        fixture.MetaDb.GetCurrentCacheLookup(
                Arg.Is<string>(key =>
                    key == PublicationApiCacheKeys.Songs))
            .Returns(new PublicationCacheLookup(
                true,
                Cached(json)));
        var nextCalled = false;
        var middleware = new PublicApiResponseCacheMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            NullLogger<
                PublicApiResponseCacheMiddleware>.Instance);
        var context = Context(
            "/api/songs",
            "/api/songs");

        await middleware.InvokeAsync(
            context,
            fixture.MetaDb,
            fixture.Gate,
            fixture.Telemetry,
            cacheService: fixture.Cache);

        Assert.False(nextCalled);
        Assert.Equal(
            StatusCodes.Status200OK,
            context.Response.StatusCode);
        Assert.Equal(
            "hit",
            context.Response.Headers[
                "X-FST-Public-Cache"]);
        Assert.Equal(
            "l2",
            context.Response.Headers[
                "X-FST-Public-Cache-Tier"]);
        Assert.Equal(
            "public, max-age=1800, stale-while-revalidate=3600",
            context.Response.Headers.CacheControl);
        Assert.Equal(
            "{\"songs\":[1]}",
            await Body(context));
        fixture.MetaDb.DidNotReceive()
            .TrySetCurrentCachedResponse(
                Arg.Any<long>(),
                Arg.Any<string>(),
                Arg.Any<byte[]>(),
                Arg.Any<string>());
    }

    [Fact]
    public async Task Frozen_miss_fails_closed_without_write_or_next()
    {
        var fixture = Fixture(
            new PublicReadFreezeState(
                true,
                DateTime.UtcNow,
                1302,
                PublicReadFreezeState
                    .MaxScoreMaintenanceReasonPrefix
                + new string('b', 64)));
        var nextCalled = false;
        var middleware = new PublicApiResponseCacheMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            NullLogger<
                PublicApiResponseCacheMiddleware>.Instance);
        var context = Context(
            "/api/songs",
            "/api/songs");

        await middleware.InvokeAsync(
            context,
            fixture.MetaDb,
            fixture.Gate,
            fixture.Telemetry,
            cacheService: fixture.Cache);

        Assert.False(nextCalled);
        Assert.Equal(
            StatusCodes.Status503ServiceUnavailable,
            context.Response.StatusCode);
        fixture.MetaDb.DidNotReceive()
            .TrySetCurrentCachedResponse(
                Arg.Any<long>(),
                Arg.Any<string>(),
                Arg.Any<byte[]>(),
                Arg.Any<string>());
    }

    [Fact]
    public async Task Lazy_build_preserves_bytes_and_etag_then_stores()
    {
        var fixture = Fixture(
            PublicReadFreezeState.NotFrozen);
        var json = Encoding.UTF8.GetBytes(
            "{\"rankBy\":\"adjusted\",\"pageSize\":25}");
        fixture.MetaDb.TrySetCurrentCachedResponse(
                42,
                Arg.Any<string>(),
                Arg.Is<byte[]>(bytes =>
                    bytes.SequenceEqual(json)),
                ResponseCacheService.ComputeETag(json))
            .Returns(Cached(json));
        var middleware = new PublicApiResponseCacheMiddleware(
            async context =>
            {
                context.Response.StatusCode =
                    StatusCodes.Status200OK;
                context.Response.ContentType =
                    "application/json; charset=utf-8";
                await context.Response.Body.WriteAsync(json);
            },
            NullLogger<
                PublicApiResponseCacheMiddleware>.Instance,
            TimeSpan.FromMilliseconds(100));
        var context = Context(
            "/api/rankings/overview?pageSize=25",
            "/api/rankings/overview");

        await middleware.InvokeAsync(
            context,
            fixture.MetaDb,
            fixture.Gate,
            fixture.Telemetry,
            cacheService: fixture.Cache);

        Assert.Equal(
            Encoding.UTF8.GetString(json),
            await Body(context));
        Assert.Equal(
            ResponseCacheService.ComputeETag(json),
            context.Response.Headers.ETag);
        Assert.Equal(
            "build",
            context.Response.Headers[
                "X-FST-Public-Cache"]);
        fixture.MetaDb.Received(1)
            .TrySetCurrentCachedResponse(
                42,
                "rankings:overview:adjusted:25",
                Arg.Any<byte[]>(),
                ResponseCacheService.ComputeETag(json));
        Assert.Contains(
            fixture.Telemetry.Snapshot().Operations,
            operation =>
                operation.Operation
                == PublicationApiCacheOperation.BuildStored);
    }

    [Fact]
    public async Task Slow_lazy_build_is_served_but_not_stored()
    {
        var fixture = Fixture(
            PublicReadFreezeState.NotFrozen);
        var middleware = new PublicApiResponseCacheMiddleware(
            async context =>
            {
                await Task.Delay(20);
                context.Response.StatusCode =
                    StatusCodes.Status200OK;
                context.Response.ContentType =
                    "application/json";
                await context.Response.WriteAsync(
                    "{\"slow\":true}");
            },
            NullLogger<
                PublicApiResponseCacheMiddleware>.Instance,
            TimeSpan.FromMilliseconds(1));
        var context = Context(
            "/api/rankings/overview?pageSize=25",
            "/api/rankings/overview");

        await middleware.InvokeAsync(
            context,
            fixture.MetaDb,
            fixture.Gate,
            fixture.Telemetry,
            cacheService: fixture.Cache);

        Assert.Equal(
            StatusCodes.Status200OK,
            context.Response.StatusCode);
        fixture.MetaDb.DidNotReceive()
            .TrySetCurrentCachedResponse(
                Arg.Any<long>(),
                Arg.Any<string>(),
                Arg.Any<byte[]>(),
                Arg.Any<string>());
        Assert.Contains(
            fixture.Telemetry.Snapshot().Operations,
            operation =>
                operation.Operation
                == PublicationApiCacheOperation
                    .BuildRejectedSlow);
    }

    [Fact]
    public async Task Concurrent_lazy_misses_compute_once()
    {
        var fixture = Fixture(
            PublicReadFreezeState.NotFrozen);
        PublicationCachedResponse? stored = null;
        fixture.MetaDb.GetCurrentCacheLookup(
                Arg.Any<string>())
            .Returns(_ => new PublicationCacheLookup(
                true,
                stored));
        fixture.MetaDb.TrySetCurrentCachedResponse(
                42,
                Arg.Any<string>(),
                Arg.Any<byte[]>(),
                Arg.Any<string>())
            .Returns(call =>
            {
                var json = call.ArgAt<byte[]>(2);
                stored = Cached(json) with
                {
                    CacheKey = call.ArgAt<string>(1),
                };
                return stored;
            });
        var builds = 0;
        var middleware = new PublicApiResponseCacheMiddleware(
            async context =>
            {
                Interlocked.Increment(ref builds);
                await Task.Delay(50);
                context.Response.StatusCode =
                    StatusCodes.Status200OK;
                context.Response.ContentType =
                    "application/json";
                await context.Response.WriteAsync(
                    "{\"singleFlight\":true}");
            },
            NullLogger<
                PublicApiResponseCacheMiddleware>.Instance);
        var first = Context(
            "/api/rankings/overview?pageSize=25",
            "/api/rankings/overview");
        var second = Context(
            "/api/rankings/overview?pageSize=25",
            "/api/rankings/overview");

        await Task.WhenAll(
            middleware.InvokeAsync(
                first,
                fixture.MetaDb,
                fixture.Gate,
                fixture.Telemetry,
                cacheService: fixture.Cache),
            middleware.InvokeAsync(
                second,
                fixture.MetaDb,
                fixture.Gate,
                fixture.Telemetry,
                cacheService: fixture.Cache));

        Assert.Equal(1, builds);
        Assert.Equal(await Body(first), await Body(second));
        Assert.Contains(
            fixture.Telemetry.Snapshot().Operations,
            operation =>
                operation.Operation
                == PublicationApiCacheOperation
                    .SingleFlightWait);
    }

    [Fact]
    public async Task Failed_build_does_not_poison_cache()
    {
        var fixture = Fixture(
            PublicReadFreezeState.NotFrozen);
        var middleware = new PublicApiResponseCacheMiddleware(
            _ => throw new InvalidOperationException(
                "injected"),
            NullLogger<
                PublicApiResponseCacheMiddleware>.Instance);
        var context = Context(
            "/api/rankings/overview?pageSize=25",
            "/api/rankings/overview");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => middleware.InvokeAsync(
                context,
                fixture.MetaDb,
                fixture.Gate,
                fixture.Telemetry,
                cacheService: fixture.Cache));

        fixture.MetaDb.DidNotReceive()
            .TrySetCurrentCachedResponse(
                Arg.Any<long>(),
                Arg.Any<string>(),
                Arg.Any<byte[]>(),
                Arg.Any<string>());
        Assert.Null(fixture.Cache.TryGetCurrent(
            Plan(context.Request)));
        Assert.Contains(
            fixture.Telemetry.Snapshot().Operations,
            operation =>
                operation.Operation
                == PublicationApiCacheOperation.BuildError
                && operation.ErrorType
                == nameof(InvalidOperationException));
    }

    [Fact]
    public void Telemetry_hashes_account_bearing_cache_keys()
    {
        var telemetry = new PublicApiCacheTelemetry();
        var context = Context(
            "/api/player/account-secret",
            "/api/player/{accountId}");

        telemetry.RecordOperation(
            context,
            "public-route:/api/player/account-secret",
            42,
            "revision",
            PublicationApiCacheOperation.L2Hit,
            TimeSpan.FromMilliseconds(2),
            128);

        var trace = Assert.Single(
            telemetry.Snapshot().Operations);
        Assert.DoesNotContain(
            "account-secret",
            trace.CacheKeyHash,
            StringComparison.Ordinal);
        Assert.Equal(16, trace.CacheKeyHash.Length);
        Assert.Equal(
            "/api/player/{accountId}",
            trace.RoutePattern);
    }

    private static CacheFixture Fixture(
        PublicReadFreezeState state)
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(state);
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var cache = new PublicationApiResponseCacheService(
            metaDb,
            gate,
            () => 42,
            NullLogger<
                PublicationApiResponseCacheService>.Instance);
        return new CacheFixture(
            metaDb,
            gate,
            cache,
            new PublicApiCacheTelemetry());
    }

    private static PublicationCachedResponse Cached(
        byte[] json) => new(
        42,
        1302,
        DateTime.UtcNow,
        json,
        ResponseCacheService.ComputeETag(json),
        DateTime.UtcNow,
        "application/json",
        Convert.ToHexString(
            System.Security.Cryptography.SHA256
                .HashData(json))
            .ToLowerInvariant());

    private static DefaultHttpContext Context(
        string target,
        string pattern)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        var separator = target.IndexOf('?');
        context.Request.Path = separator < 0
            ? target
            : target[..separator];
        if (separator >= 0)
        {
            context.Request.QueryString =
                new QueryString(target[separator..]);
        }
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(pattern),
            0,
            new EndpointMetadataCollection(
                new HttpMethodMetadata([HttpMethods.Get]),
                PublicationBound.Instance),
            pattern));
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> Body(
        HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(
            context.Response.Body,
            Encoding.UTF8,
            leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static PublicApiCacheRequestPlan Plan(
        HttpRequest request)
    {
        Assert.True(
            PublicApiResponseCachePolicy
                .TryCreateRequestPlan(
                    request,
                    out var plan));
        return plan;
    }

    private sealed record CacheFixture(
        IMetaDatabase MetaDb,
        PublicReadGateService Gate,
        PublicationApiResponseCacheService Cache,
        PublicApiCacheTelemetry Telemetry);
}
