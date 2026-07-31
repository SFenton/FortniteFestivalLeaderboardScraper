using FSTService.Api;
using FSTService.Persistence;
using FSTService.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
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
    public void MetaDatabase_PublicReadFreeze_without_explicit_id_pins_published_scrape()
    {
        using var fixture = new InMemoryMetaDatabase();
        var metaDb = fixture.Db;
        var publishedId = metaDb.StartScrapeRun();
        metaDb.CompleteScrapeRun(publishedId, 1, 1, 1, 1);
        metaDb.PublishScrapeRun(publishedId, promoteCachedResponses: false);

        metaDb.SetPublicReadFreeze(true, reason: "scrape");

        var frozen = metaDb.GetPublicReadFreezeState();
        Assert.True(frozen.IsFrozen);
        Assert.Equal(publishedId, frozen.ScrapeId);

        metaDb.SetPublicReadFreeze(true, reason: "post-process");
        var postProcess = metaDb.GetPublicReadFreezeState();
        Assert.Equal(frozen.FrozenAt, postProcess.FrozenAt);
        Assert.Equal("post-process", postProcess.Reason);
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

    [Theory]
    [InlineData(MetaDatabase.FailedCandidateReadIsolationFailurePhase)]
    [InlineData(MetaDatabase.NoProgressReadIsolationFailurePhase)]
    [InlineData(MetaDatabase.PostProcessReadIsolationFailurePhase)]
    [InlineData(MetaDatabase.PublicationReadIsolationFailurePhase)]
    public void MetaDatabase_FailedCandidateReadIsolation_PersistsUntilLaterPublication(
        string failurePhase)
    {
        using var fixture = new InMemoryMetaDatabase();
        var metaDb = fixture.Db;
        var publishedId = metaDb.StartScrapeRun();
        metaDb.CompleteScrapeRun(publishedId, 1, 1, 1, 1);
        metaDb.PublishScrapeRun(publishedId, promoteCachedResponses: false);

        var failedId = metaDb.StartScrapeRun();
        metaDb.FailScrapeRun(
            failedId,
            failurePhase,
            "derived state changed before the candidate was abandoned");

        var isolation = metaDb.GetFailedCandidateReadIsolationState();
        Assert.True(isolation.IsFrozen);
        Assert.Equal(failedId, isolation.ScrapeId);
        Assert.Equal(MetaDatabase.FailedCandidateReadIsolationReason, isolation.Reason);

        var nextPublishedId = metaDb.StartScrapeRun();
        metaDb.CompleteScrapeRun(nextPublishedId, 1, 1, 1, 1);
        metaDb.PublishScrapeRun(nextPublishedId, promoteCachedResponses: false);

        Assert.False(metaDb.GetFailedCandidateReadIsolationState().IsFrozen);
    }

    [Fact]
    public void ResponseCache_AllowsCacheMissesDuringPublicReadFreeze()
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

        Assert.True(cache.IsFrozen);
        Assert.False(cache.RequiresCachedReads);
        Assert.Null(CacheHelper.ServeUnavailableIfFrozen(new DefaultHttpContext(), cache));

        cache.Freeze();

        Assert.True(cache.IsFrozen);
        Assert.False(cache.RequiresCachedReads);
        Assert.Null(CacheHelper.ServeUnavailableIfFrozen(new DefaultHttpContext(), cache));
    }

    [Fact]
    public void ResponseCache_RequiresCachedReadsForFailedCandidateIsolation()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(PublicReadFreezeState.NotFrozen);
        metaDb.GetFailedCandidateReadIsolationState().Returns(
            new PublicReadFreezeState(true, DateTime.UtcNow, 1263, MetaDatabase.FailedCandidateReadIsolationReason));
        var gate = new PublicReadGateService(metaDb, NullLogger<PublicReadGateService>.Instance);

        using var cache = new ResponseCacheService(TimeSpan.FromMinutes(5), gate);

        Assert.True(cache.IsFrozen);
        Assert.True(cache.RequiresCachedReads);
        Assert.NotNull(CacheHelper.ServeUnavailableIfFrozen(new DefaultHttpContext(), cache));
    }

    [Fact]
    public void ResponseCache_CanRequireCachedReadsDuringNormalFreeze()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(
            new PublicReadFreezeState(true, DateTime.UtcNow, 1271, "scrape"));
        var gate = new PublicReadGateService(metaDb, NullLogger<PublicReadGateService>.Instance);

        using var cache = new ResponseCacheService(
            TimeSpan.FromMinutes(5),
            gate,
            requireCachedReadsWhenFrozen: true);

        Assert.True(cache.IsFrozen);
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

    [Fact]
    public void PublicReadGate_FailsDerivedReadsClosedWhenSafetyStateCannotBeRead()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(_ => throw new InvalidOperationException("database unavailable"));
        var gate = new PublicReadGateService(metaDb, NullLogger<PublicReadGateService>.Instance);

        Assert.True(gate.IsFrozen);
        Assert.True(gate.RequiresCachedReads);
        Assert.Equal("read-safety-state-unavailable", gate.GetState().Reason);
    }

    [Theory]
    [InlineData("/api/player/account/notifications", false)]
    [InlineData("/api/rankings/bands/Band_Duets/team/notifications", true)]
    [InlineData("/api/bands/band-id/notifications", true)]
    [InlineData("/api/player/account/export", true)]
    [InlineData("/api/bands/Band_Duets/team/export", true)]
    [InlineData("/api/leaderboard-population", true)]
    [InlineData("/api/rankings/Solo_Guitar", true)]
    [InlineData("/api/leaderboard/song/Solo_Guitar", false)]
    [InlineData("/api/leaderboard/song/bands/all", true)]
    [InlineData("/api/player/account/stats", true)]
    [InlineData("/api/bands/search", true)]
    [InlineData("/api/songs", false)]
    [InlineData("/api/songs/member-score-filter", true)]
    [InlineData("/api/status", true)]
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
    public async Task PublicReadGateMiddleware_AllowsClassifiedRoutesDuringPublishFreeze()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(new PublicReadFreezeState(true, DateTime.UtcNow, 794, "publish"));
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
    Assert.Equal("published", context.Response.Headers["X-FST-Public-Read-Mode"]);
    Assert.Equal("publish", context.Response.Headers["X-FST-Public-Read-Freeze-Reason"]);
    }

    [Fact]
    public async Task PublicReadGateMiddleware_AddsPublishedModeHeadersForFrozenApiRoutes()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(new PublicReadFreezeState(true, DateTime.UtcNow, 794, "publish"));
        var gate = new PublicReadGateService(metaDb, NullLogger<PublicReadGateService>.Instance);
        var middleware = new PublicReadGateMiddleware(context =>
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/rankings/composite/account-1";
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, gate);

        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        Assert.Equal("published", context.Response.Headers["X-FST-Public-Read-Mode"]);
        Assert.Equal("publish", context.Response.Headers["X-FST-Public-Read-Freeze-Reason"]);
    }

    [Fact]
    public async Task PublicReadGateMiddleware_FailsClosedForUncachedFailedCandidateDerivedRoute()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(PublicReadFreezeState.NotFrozen);
        metaDb.GetFailedCandidateReadIsolationState().Returns(
            new PublicReadFreezeState(true, DateTime.UtcNow, 1263, MetaDatabase.FailedCandidateReadIsolationReason));
        var gate = new PublicReadGateService(metaDb, NullLogger<PublicReadGateService>.Instance);
        var nextCalled = false;
        var middleware = new PublicReadGateMiddleware(context =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/player/account/export";
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, gate);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("published", context.Response.Headers["X-FST-Public-Read-Mode"]);
        Assert.Equal(
            MetaDatabase.FailedCandidateReadIsolationReason,
            context.Response.Headers["X-FST-Public-Read-Freeze-Reason"]);
    }

    [Fact]
    public async Task PublicReadGateMiddleware_AllowsMappedSoloLeaderboardDuringFailedCandidateIsolation()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(PublicReadFreezeState.NotFrozen);
        metaDb.GetFailedCandidateReadIsolationState().Returns(
            new PublicReadFreezeState(true, DateTime.UtcNow, 1263, MetaDatabase.FailedCandidateReadIsolationReason));
        var gate = new PublicReadGateService(metaDb, NullLogger<PublicReadGateService>.Instance);
        var nextCalled = false;
        var middleware = new PublicReadGateMiddleware(context =>
        {
            nextCalled = true;
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/leaderboard/song-1/Solo_Guitar";
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, gate);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/rankings/Solo_Guitar", true)]
    [InlineData("/api/leaderboard/song_1/bands/all", true)]
    [InlineData("/api/player/account/rivals", true)]
    [InlineData("/api/player/account/notifications", false)]
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

    [Theory]
    [InlineData("?accountId=selectedAcct")]
    [InlineData("?teamKey=selectedAcct%3AselectedMate")]
    [InlineData("?selectedBandType=Band_Duets&selectedTeamKey=selectedAcct%3AselectedMate")]
    public void PublicApiResponseCachePolicy_DoesNotCacheSelectedOverlayQueries(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/leaderboard/song_1/bands/all";
        context.Request.QueryString = new QueryString(query);

        Assert.False(PublicApiResponseCachePolicy.IsCacheableRequest(context.Request, out _));
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
    public async Task PublicApiResponseCacheMiddleware_DoesNotStoreSuccessfulJsonResponseWhenNotFrozen()
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
        SetPublicationEndpoint(context, "/api/rankings/{instrument}");
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        var telemetry = new PublicApiCacheTelemetry();

        await middleware.InvokeAsync(context, metaDb, gate, telemetry);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        Assert.Equal("{\"ok\":true}", await reader.ReadToEndAsync());
        metaDb.DidNotReceive().BulkSetCachedResponses(Arg.Any<IEnumerable<(string Key, byte[] Json, string ETag)>>());
        Assert.False(context.Response.Headers.ContainsKey("X-FST-Public-Cache"));
        Assert.Equal(0, telemetry.Snapshot().Total);
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
        SetPublicationEndpoint(context, "/api/rankings/{instrument}");
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        var telemetry = new PublicApiCacheTelemetry();

        await middleware.InvokeAsync(context, metaDb, gate, telemetry);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        Assert.False(nextCalled);
        Assert.Equal("{\"publishedScrapeId\":793}", await reader.ReadToEndAsync());
        Assert.Equal("hit", context.Response.Headers["X-FST-Public-Cache"]);
        Assert.Equal(1, telemetry.Snapshot().Hits);
    }

    [Fact]
    public async Task PublicApiResponseCacheMiddleware_ContinuesOnFrozenCacheMiss()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(new PublicReadFreezeState(true, DateTime.UtcNow, 793, "publish"));
        metaDb.GetCachedResponse(Arg.Any<string>()).Returns(((byte[] Json, string ETag)?)null);
        var gate = new PublicReadGateService(metaDb, NullLogger<PublicReadGateService>.Instance);
        var nextCalled = false;
        var middleware = new PublicApiResponseCacheMiddleware(async context =>
        {
            nextCalled = true;
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"computed\":true}");
        }, NullLogger<PublicApiResponseCacheMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/rankings/composite/account-1";
        SetPublicationEndpoint(context, "/api/rankings/composite/{accountId}");
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        var telemetry = new PublicApiCacheTelemetry();

        await middleware.InvokeAsync(context, metaDb, gate, telemetry);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("miss", context.Response.Headers["X-FST-Public-Cache"]);
        Assert.Equal("{\"computed\":true}", await reader.ReadToEndAsync());
        metaDb.DidNotReceive().BulkSetCachedResponses(Arg.Any<IEnumerable<(string Key, byte[] Json, string ETag)>>());
        Assert.Equal(1, telemetry.Snapshot().MissesContinued);
    }

    [Fact]
    public async Task PublicApiResponseCacheMiddleware_FailsClosedOnFailedCandidateCacheMiss()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(PublicReadFreezeState.NotFrozen);
        metaDb.GetFailedCandidateReadIsolationState().Returns(
            new PublicReadFreezeState(true, DateTime.UtcNow, 1263, MetaDatabase.FailedCandidateReadIsolationReason));
        metaDb.GetCachedResponse(Arg.Any<string>()).Returns(((byte[] Json, string ETag)?)null);
        var gate = new PublicReadGateService(metaDb, NullLogger<PublicReadGateService>.Instance);
        var nextCalled = false;
        var middleware = new PublicApiResponseCacheMiddleware(context =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, NullLogger<PublicApiResponseCacheMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/rankings/Solo_Guitar/account-1";
        SetPublicationEndpoint(context, "/api/rankings/{instrument}/{accountId}");
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        var telemetry = new PublicApiCacheTelemetry();

        await middleware.InvokeAsync(context, metaDb, gate, telemetry);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("miss", context.Response.Headers["X-FST-Public-Cache"]);
        Assert.Equal(1, telemetry.Snapshot().MissesBlocked);
    }

    [Fact]
    public async Task PublicApiResponseCacheMiddleware_RecordsPublicationBoundBypassDuringFreeze()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(
            new PublicReadFreezeState(true, DateTime.UtcNow, 793, "scrape"));
        var gate = new PublicReadGateService(metaDb, NullLogger<PublicReadGateService>.Instance);
        var nextCalled = false;
        var middleware = new PublicApiResponseCacheMiddleware(context =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, NullLogger<PublicApiResponseCacheMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/paths/song/Solo_Guitar/expert";
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/api/paths/{songId}/{instrument}/{difficulty}"),
            order: 0,
            new EndpointMetadataCollection(
                new HttpMethodMetadata([HttpMethods.Get]),
                PublicationBound.Instance),
            displayName: "path"));
        var telemetry = new PublicApiCacheTelemetry();

        await middleware.InvokeAsync(context, metaDb, gate, telemetry);

        Assert.True(nextCalled);
        var snapshot = telemetry.Snapshot();
        Assert.Equal(1, snapshot.Bypassed);
        var route = Assert.Single(snapshot.Routes);
        Assert.Equal("/api/paths/{songId}/{instrument}/{difficulty}", route.RoutePattern);
        Assert.Equal(nameof(PublicationBound), route.Classification);
    }

    [Fact]
    public async Task PublicApiResponseCacheMiddleware_WebSocketBypassesGateAndTelemetry()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        var gate = new PublicReadGateService(metaDb, NullLogger<PublicReadGateService>.Instance);
        var nextCalled = false;
        var middleware = new PublicApiResponseCacheMiddleware(context =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, NullLogger<PublicApiResponseCacheMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/ws";
        var webSocketFeature = Substitute.For<IHttpWebSocketFeature>();
        webSocketFeature.IsWebSocketRequest.Returns(true);
        context.Features.Set(webSocketFeature);
        SetPublicationEndpoint(context, "/api/ws", ApiPublicationRouteCatalog.AnyMethod);
        var telemetry = new PublicApiCacheTelemetry();

        await middleware.InvokeAsync(context, metaDb, gate, telemetry);

        Assert.True(nextCalled);
        Assert.Equal(0, telemetry.Snapshot().Total);
        metaDb.DidNotReceive().GetPublicReadFreezeState();
        metaDb.DidNotReceive().GetFailedCandidateReadIsolationState();
    }

    [Fact]
    public async Task PublicApiResponseCacheMiddleware_DoesNotCountAdminFallbackTraffic()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(
            new PublicReadFreezeState(true, DateTime.UtcNow, 793, "scrape"));
        metaDb.GetCachedResponse(Arg.Any<string>()).Returns(((byte[] Json, string ETag)?)null);
        var gate = new PublicReadGateService(metaDb, NullLogger<PublicReadGateService>.Instance);
        var middleware = new PublicApiResponseCacheMiddleware(
            _ => Task.CompletedTask,
            NullLogger<PublicApiResponseCacheMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/scanner-generated-path";
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/api/{**path}"),
            order: int.MaxValue,
            new EndpointMetadataCollection(
                new AdminPrivate("test fallback"),
                new HttpMethodMetadata([ApiPublicationRouteCatalog.AnyMethod])),
            displayName: "fallback"));
        var telemetry = new PublicApiCacheTelemetry();

        await middleware.InvokeAsync(context, metaDb, gate, telemetry);

        Assert.Equal(0, telemetry.Snapshot().Total);
    }

    private static void SetPublicationEndpoint(
        HttpContext context,
        string routePattern,
        string method = "GET")
    {
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(routePattern),
            order: 0,
            new EndpointMetadataCollection(
                new HttpMethodMetadata([method]),
                PublicationBound.Instance),
            displayName: routePattern));
    }
}