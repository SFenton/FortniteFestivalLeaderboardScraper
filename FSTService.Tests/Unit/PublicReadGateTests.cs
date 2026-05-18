using FSTService.Api;
using FSTService.Persistence;
using FSTService.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Text;

namespace FSTService.Tests.Unit;

public class PublicReadGateTests
{
    [Fact]
    public void MetaDatabase_PublicReadFreeze_RoundTripsAndPublishClears()
    {
        using var fixture = new InMemoryMetaDatabase();
        var metaDb = fixture.Db;
        var scrapeId = metaDb.StartScrapeRun();

        metaDb.SetPublicReadFreeze(true, scrapeId, "test");

        var frozen = metaDb.GetPublicReadFreezeState();
        Assert.True(frozen.IsFrozen);
        Assert.Equal(scrapeId, frozen.ScrapeId);
        Assert.Equal("test", frozen.Reason);
        Assert.NotNull(frozen.FrozenAt);

        metaDb.SetPublicReadFreeze(false);
        Assert.False(metaDb.GetPublicReadFreezeState().IsFrozen);

        metaDb.SetPublicReadFreeze(true, scrapeId, "test");

        metaDb.CompleteScrapeRun(scrapeId, 0, 0, 0, 0);
        metaDb.PublishScrapeRun(scrapeId, promoteCachedResponses: false);

        Assert.False(metaDb.GetPublicReadFreezeState().IsFrozen);
    }

    [Fact]
    public void ResponseCache_UsesPublicReadGateFreezeState()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(new PublicReadFreezeState(true, DateTime.UtcNow, null, "test"));
        var gate = new PublicReadGateService(metaDb, NullLogger<PublicReadGateService>.Instance);

        using var cache = new ResponseCacheService(TimeSpan.FromMinutes(5), gate);

        Assert.True(cache.IsFrozen);
    }

    [Fact]
    public void ResponseCache_AllowsCacheMissesDuringScrapeFreezeOnly()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(new PublicReadFreezeState(true, DateTime.UtcNow, 794, "scrape"));
        var gate = new PublicReadGateService(metaDb, NullLogger<PublicReadGateService>.Instance);

        using var cache = new ResponseCacheService(TimeSpan.FromMinutes(5), gate);

        Assert.True(cache.IsFrozen);
        Assert.False(cache.RequiresCachedReads);
        Assert.Null(CacheHelper.ServeUnavailableIfFrozen(new DefaultHttpContext(), cache));

        metaDb.GetPublicReadFreezeState().Returns(new PublicReadFreezeState(true, DateTime.UtcNow, 794, "publish"));
        gate.Invalidate();

        Assert.True(cache.RequiresCachedReads);
        Assert.NotNull(CacheHelper.ServeUnavailableIfFrozen(new DefaultHttpContext(), cache));
    }

    [Fact]
    public void PublicReadGate_CachesUntilInvalidated()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(
            PublicReadFreezeState.NotFrozen,
            new PublicReadFreezeState(true, DateTime.UtcNow, null, "test"));
        var gate = new PublicReadGateService(metaDb, NullLogger<PublicReadGateService>.Instance);

        Assert.False(gate.IsFrozen);
        Assert.False(gate.IsFrozen);

        gate.Invalidate();

        Assert.True(gate.IsFrozen);
        metaDb.Received(2).GetPublicReadFreezeState();
    }

    [Theory]
    [InlineData("/api/player/account/notifications", true)]
    [InlineData("/api/rankings/bands/Band_Duets/team/notifications", true)]
    [InlineData("/api/bands/band-id/notifications", true)]
    [InlineData("/api/player/account/export", false)]
    [InlineData("/api/bands/Band_Duets/team/export", false)]
    [InlineData("/api/leaderboard-population", true)]
    [InlineData("/api/rankings/Solo_Guitar", false)]
    [InlineData("/api/leaderboard/song/Solo_Guitar", false)]
    [InlineData("/api/player/account/stats", false)]
    [InlineData("/api/bands/search", false)]
    [InlineData("/api/songs", false)]
    [InlineData("/api/status", false)]
    [InlineData("/api/progress", false)]
    [InlineData("/api/player/account/track", false)]
    [InlineData("/api/player/account/sync-status", false)]
    [InlineData("/api/shop", false)]
    [InlineData("/api/account/search", false)]
    public void RequiresPublishedData_ClassifiesRankDerivedRoutes(string path, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        Assert.Equal(expected, PublicReadGateMiddleware.RequiresPublishedData(context.Request));
    }

    [Fact]
    public async Task PublicReadGateMiddleware_AllowsClassifiedRoutesDuringScrapeFreeze()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(new PublicReadFreezeState(true, DateTime.UtcNow, 794, "scrape"));
        var gate = new PublicReadGateService(metaDb, NullLogger<PublicReadGateService>.Instance);
        var nextCalled = false;
        var middleware = new PublicReadGateMiddleware(context =>
        {
            nextCalled = true;
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/leaderboard-population";
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, gate);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }

    [Fact]
    public async Task PublicReadGateMiddleware_BlocksClassifiedRoutesDuringPublishFreeze()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(new PublicReadFreezeState(true, DateTime.UtcNow, 794, "publish"));
        var gate = new PublicReadGateService(metaDb, NullLogger<PublicReadGateService>.Instance);
        var nextCalled = false;
        var middleware = new PublicReadGateMiddleware(context =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/leaderboard-population";
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, gate);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/rankings/Solo_Guitar", true)]
    [InlineData("/api/leaderboard/song_1/bands/all", true)]
    [InlineData("/api/player/account/rivals", true)]
    [InlineData("/api/player/account/notifications", true)]
    [InlineData("/api/leaderboard-population", true)]
    [InlineData("/api/songs/member-score-filter", true)]
    [InlineData("/api/songs", false)]
    [InlineData("/api/shop", false)]
    [InlineData("/api/paths/song/Solo_Guitar/Expert", false)]
    [InlineData("/api/status", false)]
    [InlineData("/api/progress", false)]
    [InlineData("/api/features", false)]
    [InlineData("/api/account/search", false)]
    [InlineData("/api/admin/dbstats/pressure", false)]
    [InlineData("/api/player/account/rivals/diagnostics", false)]
    [InlineData("/api/player/account/sync-status", false)]
    [InlineData("/api/player/account/export", false)]
    public void PublicApiResponseCachePolicy_ClassifiesPublicFrozenFallbackRoutes(string path, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;

        Assert.Equal(expected, PublicApiResponseCachePolicy.IsCacheableRequest(context.Request, out _));
    }

    [Fact]
    public void PublicApiResponseCachePolicy_KeyVariesBySelectedProfileHeaders()
    {
        var playerContext = new DefaultHttpContext();
        playerContext.Request.Method = HttpMethods.Get;
        playerContext.Request.Path = "/api/rankings/selected-members";
        playerContext.Request.QueryString = new QueryString("?rankBy=adjusted");
        playerContext.Request.Headers[SelectedProfileHeaders.SelectedProfileTypeHeader] = "player";
        playerContext.Request.Headers[SelectedProfileHeaders.SelectedProfileIdHeader] = "account-1";

        var bandContext = new DefaultHttpContext();
        bandContext.Request.Method = HttpMethods.Get;
        bandContext.Request.Path = "/api/rankings/selected-members";
        bandContext.Request.QueryString = new QueryString("?rankBy=adjusted");
        bandContext.Request.Headers[SelectedProfileHeaders.SelectedProfileTypeHeader] = "band";
        bandContext.Request.Headers[SelectedProfileHeaders.SelectedProfileIdHeader] = "band-1";
        bandContext.Request.Headers[SelectedProfileHeaders.SelectedBandTypeHeader] = "Band_Duets";
        bandContext.Request.Headers[SelectedProfileHeaders.SelectedBandTeamKeyHeader] = "p1:p2";

        var playerKey = PublicApiResponseCachePolicy.BuildCacheKey(playerContext.Request);
        var bandKey = PublicApiResponseCachePolicy.BuildCacheKey(bandContext.Request);

        Assert.NotEqual(playerKey, bandKey);
        Assert.Contains("profileType=player", playerKey);
        Assert.Contains("profileType=band", bandKey);
        Assert.Contains("teamKey=p1:p2", bandKey);
    }

    [Fact]
    public async Task PublicApiResponseCacheMiddleware_StoresSuccessfulJsonResponseWhenNotFrozen()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(PublicReadFreezeState.NotFrozen);
        var gate = new PublicReadGateService(metaDb, NullLogger<PublicReadGateService>.Instance);
        var middleware = new PublicApiResponseCacheMiddleware(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"ok\":true}");
        }, NullLogger<PublicApiResponseCacheMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/rankings/Solo_Guitar";
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, metaDb, gate);

        metaDb.Received(1).BulkSetCachedResponses(Arg.Is<IEnumerable<(string Key, byte[] Json, string ETag)>>(entries =>
            entries.Single().Key.StartsWith("public-route:/api/rankings/Solo_Guitar", StringComparison.Ordinal) &&
            Encoding.UTF8.GetString(entries.Single().Json) == "{\"ok\":true}"));
    }

    [Fact]
    public async Task PublicApiResponseCacheMiddleware_ServesPersistedJsonWhenFrozen()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(new PublicReadFreezeState(true, DateTime.UtcNow, 793, "test"));
        var json = Encoding.UTF8.GetBytes("{\"publishedScrapeId\":793}");
        metaDb.GetCachedResponse(Arg.Any<string>()).Returns((json, ResponseCacheService.ComputeETag(json)));
        var gate = new PublicReadGateService(metaDb, NullLogger<PublicReadGateService>.Instance);
        var nextCalled = false;
        var middleware = new PublicApiResponseCacheMiddleware(context =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, NullLogger<PublicApiResponseCacheMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/rankings/Solo_Guitar";
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, metaDb, gate);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        Assert.False(nextCalled);
        Assert.Equal("{\"publishedScrapeId\":793}", await reader.ReadToEndAsync());
        Assert.Equal("hit", context.Response.Headers["X-FST-Public-Cache"]);
    }
}