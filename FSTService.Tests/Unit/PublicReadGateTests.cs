using FSTService.Api;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Npgsql;
using System.Text;

namespace FSTService.Tests.Unit;

public class PublicReadGateTests
{
    [Theory]
    [InlineData("path-repair-ranking-rebuild")]
    [InlineData("path-repair-ranking-alignment")]
    [InlineData("max-score-maintenance:v1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void HistoricalSamePublicationMaintenanceFreezeRequiresRefresh(
        string reason)
    {
        var freeze = new PublicReadFreezeState(
            true,
            DateTime.UtcNow,
            1276,
            reason);

        Assert.True(freeze.RequiresSamePublicationRefreshOnRelease);
    }

    [Fact]
    public void MaxScoreMaintenanceFreezeCannotBeClearedByGenericFreezeWriter()
    {
        using var fixture = new InMemoryMetaDatabase();
        using (var conn = fixture.DataSource.OpenConnection())
        using (var seed = conn.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO scrape_log (
                    id, started_at, completed_at, status)
                VALUES
                    (1296, now(), now(), 'completed'),
                    (1297, now(), now(), 'completed')
                """;
            seed.ExecuteNonQuery();
        }
        var reason =
            PublicReadFreezeState.MaxScoreMaintenanceReasonPrefix
            + new string('a', 64);
        fixture.Db.SetPublicReadFreeze(
            true,
            1296,
            reason);

        fixture.Db.SetPublicReadFreeze(false);
        fixture.Db.SetPublicReadFreeze(
            true,
            1297,
            "scrape");

        var freeze = fixture.Db.GetPublicReadFreezeState();
        Assert.True(freeze.IsFrozen);
        Assert.Equal(1296, freeze.ScrapeId);
        Assert.Equal(reason, freeze.Reason);
    }

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
    public void ResponseCache_DiscardsProcessEntriesDuringFailedCandidateIsolation()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(
            PublicReadFreezeState.NotFrozen);
        metaDb.GetFailedCandidateReadIsolationState().Returns(
            PublicReadFreezeState.NotFrozen);
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        using var cache = new ResponseCacheService(
            TimeSpan.FromMinutes(5),
            gate);
        var publishedJson = Encoding.UTF8.GetBytes(
            """{"source":"published"}""");

        cache.Set("player:account:::", publishedJson);
        Assert.NotNull(cache.Get("player:account:::"));

        metaDb.GetFailedCandidateReadIsolationState().Returns(
            new PublicReadFreezeState(
                true,
                DateTime.UtcNow,
                1263,
                MetaDatabase.FailedCandidateReadIsolationReason));

        Assert.Null(cache.Get("player:account:::"));
        cache.Set(
            "player:account:::",
            Encoding.UTF8.GetBytes("""{"source":"candidate"}"""));

        metaDb.GetFailedCandidateReadIsolationState().Returns(
            PublicReadFreezeState.NotFrozen);
        gate.Invalidate();

        Assert.Null(cache.Get("player:account:::"));
    }

    [Fact]
    public void ResponseCache_DiscardsPreFreezeEntryAfterUnfreeze()
    {
        var freeze = PublicReadFreezeState.NotFrozen;
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(_ => freeze);
        metaDb.GetFailedCandidateReadIsolationState().Returns(
            PublicReadFreezeState.NotFrozen);
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        using var cache = new ResponseCacheService(
            TimeSpan.FromMinutes(5),
            gate);

        cache.Set(
            "player:account:::",
            Encoding.UTF8.GetBytes("""{"source":"pre-freeze"}"""));
        freeze = new PublicReadFreezeState(
            true,
            DateTime.UtcNow,
            1274,
            "maintenance");
        gate.Invalidate();
        Assert.NotNull(cache.Get("player:account:::"));

        freeze = PublicReadFreezeState.NotFrozen;
        gate.Invalidate();
        Assert.Null(cache.Get("player:account:::"));
    }

    [Fact]
    public void ResponseCache_RejectsEntriesFromAnotherPublication()
    {
        long? publicationId = 1;
        using var cache = new ResponseCacheService(
            TimeSpan.FromMinutes(5),
            publicationIdProvider: () => publicationId);
        var json = Encoding.UTF8.GetBytes("""{"publicationId":1}""");

        cache.Set("player:account:::", json);
        Assert.NotNull(cache.Get("player:account:::"));

        publicationId = 2;

        Assert.Null(cache.Get("player:account:::"));
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
    public void PublicReadGate_RechecksPermissiveStateWithoutInvalidation()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(
            PublicReadFreezeState.NotFrozen,
            new PublicReadFreezeState(true, DateTime.UtcNow, null, "test"));
        var gate = new PublicReadGateService(metaDb, NullLogger<PublicReadGateService>.Instance);

        Assert.False(gate.IsFrozen);
        Assert.True(gate.IsFrozen);
        metaDb.Received(2).GetPublicReadFreezeState();
    }

    [Fact]
    public void PublicReadGate_ActivatesFailedCandidateIsolationWithoutManualInvalidation()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(
            new PublicReadFreezeState(
                true,
                DateTime.UtcNow,
                1263,
                "post-process"));
        metaDb.GetFailedCandidateReadIsolationState().Returns(
            PublicReadFreezeState.NotFrozen);
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);

        Assert.False(gate.RequiresCachedReads);

        metaDb.GetFailedCandidateReadIsolationState().Returns(
            new PublicReadFreezeState(
                true,
                DateTime.UtcNow,
                1263,
                MetaDatabase.FailedCandidateReadIsolationReason));

        Assert.True(gate.RequiresCachedReads);
        Assert.True(gate.FailedCandidateIsolationActive);
    }

    [Fact]
    public void PublicReadGate_InvalidationDoesNotClearFailClosedFlags()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(
            PublicReadFreezeState.NotFrozen);
        metaDb.GetFailedCandidateReadIsolationState().Returns(
            new PublicReadFreezeState(
                true,
                DateTime.UtcNow,
                1263,
                MetaDatabase.FailedCandidateReadIsolationReason));
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);

        Assert.True(gate.RequiresCachedReads);

        gate.Invalidate();

        Assert.True(gate.RequiresCachedReads);
        Assert.True(gate.FailedCandidateIsolationActive);
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
    [InlineData("/api/paths/song/Solo_Guitar/expert", false)]
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

    [Theory]
    [InlineData("/api/songs", true)]
    [InlineData("/api/paths/song/Solo_Guitar/expert", true)]
    [InlineData("/api/leaderboard/song/Solo_Guitar", true)]
    [InlineData("/api/rankings/Solo_Guitar", false)]
    public void RequiresMaxScoreMaintenanceData_ClassifiesDependentRoutes(
        string path,
        bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        Assert.Equal(
            expected,
            PublicReadGateMiddleware
                .RequiresMaxScoreMaintenanceData(
                    context.Request));
    }

    [Theory]
    [InlineData("/api/songs")]
    [InlineData("/api/paths/song/Solo_Guitar/expert")]
    [InlineData("/api/leaderboard/song/Solo_Guitar")]
    public async Task PublicationReadContextMiddleware_MaxScoreMaintenanceDefersBeforeLease(
        string path)
    {
        using var dataSource =
            SharedPostgresContainer.CreateDatabase();
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(
            new PublicReadFreezeState(
                true,
                DateTime.UtcNow,
                1302,
                PublicReadFreezeState
                    .MaxScoreMaintenanceReasonPrefix
                + new string('a', 64)));
        metaDb.GetFailedCandidateReadIsolationState()
            .Returns(PublicReadFreezeState.NotFrozen);
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var publicationService =
            new PublicationReadContextService(
                metaDb,
                dataSource,
                Options.Create(new FeatureOptions
                {
                    EnablePublicationReadContext = true,
                }));
        using var lockConnection =
            dataSource.OpenConnection();
        using var lockTransaction =
            lockConnection.BeginTransaction();
        using (var publicationLock =
               lockConnection.CreateCommand())
        {
            publicationLock.Transaction =
                lockTransaction;
            publicationLock.CommandText =
                "SELECT pg_advisory_xact_lock(@lockKey)";
            publicationLock.Parameters.AddWithValue(
                "lockKey",
                PublicationGenerationSchema
                    .AdvisoryLockKey);
            publicationLock.ExecuteNonQuery();
        }
        var nextCalled = false;
        var middleware =
            new PublicationReadContextMiddleware(
                context =>
                {
                    nextCalled = true;
                    context.Response.StatusCode =
                        StatusCodes.Status204NoContent;
                    return Task.CompletedTask;
                });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        SetPublicationEndpoint(
            context,
            path);
        context.RequestServices =
            new ServiceCollection()
                .AddLogging()
                .BuildServiceProvider();

        await middleware.InvokeAsync(
                context,
                publicationService,
                gate)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(nextCalled);
        Assert.Equal(
            StatusCodes.Status204NoContent,
            context.Response.StatusCode);
        lockTransaction.Rollback();
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
        SetPublicationEndpoint(
            context,
            "/api/player/{accountId}/export");
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

    [Theory]
    [InlineData(
        "/api/paths/song/Solo_Guitar/expert",
        "/api/paths/{songId}/{instrument}/{difficulty}",
        true)]
    [InlineData(
        "/api/leaderboard/song/Solo_Guitar",
        "/api/leaderboard/{songId}/{instrument}",
        false)]
    [InlineData(
        "/api/rankings/Solo_Guitar",
        "/api/rankings/{instrument}",
        false)]
    public async Task PublicReadGateMiddleware_RoutesMaxScoreMaintenanceReadsToCacheOrEndpointGate(
        string path,
        string pattern,
        bool endpointHandlesRead)
    {
        var reason =
            PublicReadFreezeState.MaxScoreMaintenanceReasonPrefix
            + new string('a', 64);
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(
            new PublicReadFreezeState(
                true,
                DateTime.UtcNow,
                1296,
                reason));
        metaDb.GetFailedCandidateReadIsolationState().Returns(
            PublicReadFreezeState.NotFrozen);
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var nextCalled = false;
        var middleware = new PublicReadGateMiddleware(
            nextContext =>
            {
                nextCalled = true;
                nextContext.Response.StatusCode =
                    StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        SetPublicationEndpoint(context, pattern);
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, gate);

        Assert.Equal(endpointHandlesRead, nextCalled);
        Assert.Equal(
            endpointHandlesRead
                ? StatusCodes.Status204NoContent
                : StatusCodes.Status503ServiceUnavailable,
            context.Response.StatusCode);
        Assert.Equal(
            reason,
            context.Response.Headers[
                "X-FST-Public-Read-Freeze-Reason"]);
    }

    [Fact]
    public async Task PublicReadGateMiddleware_AllowsSongsEndpointToServeCachedMaintenanceRead()
    {
        var reason =
            PublicReadFreezeState.MaxScoreMaintenanceReasonPrefix
            + new string('b', 64);
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(
            new PublicReadFreezeState(
                true,
                DateTime.UtcNow,
                1296,
                reason));
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var nextCalled = false;
        var middleware = new PublicReadGateMiddleware(context =>
        {
            nextCalled = true;
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/songs";
        SetPublicationEndpoint(context, "/api/songs");
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, gate);

        Assert.True(nextCalled);
        Assert.Equal(
            StatusCodes.Status200OK,
            context.Response.StatusCode);
    }

    [Theory]
    [InlineData(
        "POST",
        "/api/player/account-1/track")]
    [InlineData(
        "GET",
        "/api/bands/Band_Duets/account-1:account-2/sync-status")]
    [InlineData(
        "POST",
        "/api/backfill/account-1")]
    public async Task PublicReadGateMiddleware_BlocksRegistrationChangesThroughoutMaxScoreResume(
        string method,
        string path)
    {
        var reason =
            PublicReadFreezeState.MaxScoreMaintenanceReasonPrefix
            + new string('c', 64);
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(
            new PublicReadFreezeState(
                true,
                DateTime.UtcNow,
                1296,
                reason));
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var nextCalled = false;
        var middleware = new PublicReadGateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        for (var resumeAttempt = 0;
             resumeAttempt < 2;
             resumeAttempt++)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            context.Request.Path = path;
            context.RequestServices = new ServiceCollection()
                .AddLogging()
                .BuildServiceProvider();
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(context, gate);

            Assert.Equal(
                StatusCodes.Status503ServiceUnavailable,
                context.Response.StatusCode);
        }

        Assert.False(nextCalled);
    }

    [Fact]
    public async Task PublicReadGateMiddleware_AllowsEndpointOwnedFailedCandidateRead()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(
            PublicReadFreezeState.NotFrozen);
        metaDb.GetFailedCandidateReadIsolationState().Returns(
            new PublicReadFreezeState(
                true,
                DateTime.UtcNow,
                1263,
                MetaDatabase.FailedCandidateReadIsolationReason));
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var nextCalled = false;
        var middleware = new PublicReadGateMiddleware(context =>
        {
            nextCalled = true;
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/player/account";
        SetPublicationEndpoint(
            context,
            "/api/player/{accountId}",
            handlesFailedCandidateRead: true);
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, gate);

        Assert.True(nextCalled);
        Assert.Equal(
            StatusCodes.Status204NoContent,
            context.Response.StatusCode);
    }

    [Fact]
    public async Task PublicReadGateMiddleware_DoesNotDelegateWhenSafetyStateIsUnavailable()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(
            _ => throw new InvalidOperationException("database unavailable"));
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var nextCalled = false;
        var middleware = new PublicReadGateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/player/account";
        SetPublicationEndpoint(
            context,
            "/api/player/{accountId}",
            handlesFailedCandidateRead: true);
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, gate);

        Assert.False(nextCalled);
        Assert.Equal(
            StatusCodes.Status503ServiceUnavailable,
            context.Response.StatusCode);
    }

    [Fact]
    public async Task PublicReadGateMiddleware_AllowsOperationalRouteDuringFailedCandidateIsolation()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(
            PublicReadFreezeState.NotFrozen);
        metaDb.GetFailedCandidateReadIsolationState().Returns(
            new PublicReadFreezeState(
                true,
                DateTime.UtcNow,
                1263,
                MetaDatabase.FailedCandidateReadIsolationReason));
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var nextCalled = false;
        var middleware = new PublicReadGateMiddleware(context =>
        {
            nextCalled = true;
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/status";
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/api/status"),
            order: 0,
            new EndpointMetadataCollection(
                new HttpMethodMetadata([HttpMethods.Get]),
                new OperationalLive("test")),
            displayName: "status"));
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, gate);

        Assert.True(nextCalled);
        Assert.Equal(
            StatusCodes.Status204NoContent,
            context.Response.StatusCode);
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
    public void PublicApiResponseCachePolicy_PerInstrumentRankingIgnoresSelectedProfileHeaders()
    {
        var plain = new DefaultHttpContext();
        plain.Request.Method = HttpMethods.Get;
        plain.Request.Path = "/api/rankings/Solo_Guitar";
        plain.Request.QueryString =
            new QueryString(
                "?rankBy=totalscore&page=1&pageSize=25");

        var selected = new DefaultHttpContext();
        selected.Request.Method = HttpMethods.Get;
        selected.Request.Path = plain.Request.Path;
        selected.Request.QueryString = plain.Request.QueryString;
        selected.Request.Headers[
            SelectedProfileHeaders.SelectedProfileTypeHeader] =
            "player";
        selected.Request.Headers[
            SelectedProfileHeaders.SelectedProfileIdHeader] =
            "account-1";
        selected.Request.Headers[
            SelectedProfileHeaders.LegacySelectedPlayerHeader] =
            "account-1";

        Assert.Equal(
            PublicApiResponseCachePolicy.BuildCacheKey(
                plain.Request),
            PublicApiResponseCachePolicy.BuildCacheKey(
                selected.Request));
    }

    [Fact]
    public void PublicApiResponseCachePolicy_KeyIgnoresPublicationPin()
    {
        var unpinned = new DefaultHttpContext();
        unpinned.Request.Method = HttpMethods.Get;
        unpinned.Request.Path = "/api/rankings/overview";
        unpinned.Request.QueryString = new QueryString("?page=2");

        var pinned = new DefaultHttpContext();
        pinned.Request.Method = HttpMethods.Get;
        pinned.Request.Path = "/api/rankings/overview";
        pinned.Request.QueryString = new QueryString("?page=2&publicationId=42");

        Assert.Equal(
            PublicApiResponseCachePolicy.BuildCacheKey(unpinned.Request),
            PublicApiResponseCachePolicy.BuildCacheKey(pinned.Request));
    }

    [Fact]
    public void PublicApiResponseCachePolicy_KeyCanonicalizesQueryOrder()
    {
        var first = new DefaultHttpContext();
        first.Request.Method = HttpMethods.Get;
        first.Request.Path = "/api/rankings/Solo_Guitar";
        first.Request.QueryString =
            new QueryString(
                "?rankBy=totalscore&page=1&pageSize=50");

        var second = new DefaultHttpContext();
        second.Request.Method = HttpMethods.Get;
        second.Request.Path = "/api/rankings/Solo_Guitar";
        second.Request.QueryString =
            new QueryString(
                "?pageSize=50&page=1&rankBy=totalscore");

        Assert.Equal(
            PublicApiResponseCachePolicy.BuildCacheKey(
                first.Request),
            PublicApiResponseCachePolicy.BuildCacheKey(
                second.Request));
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
    public async Task PublicApiResponseCacheMiddleware_ServesExactSoloLeaderboardDuringMaxScoreMaintenance()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(
            new PublicReadFreezeState(
                true,
                DateTime.UtcNow,
                1296,
                PublicReadFreezeState
                    .MaxScoreMaintenanceReasonPrefix
                + new string('d', 64)));
        var json = Encoding.UTF8.GetBytes(
            "{\"songId\":\"song\",\"instrument\":\"Solo_Guitar\"}");
        metaDb.GetCachedResponse(Arg.Any<string>())
            .Returns((
                json,
                ResponseCacheService.ComputeETag(json)));
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var middleware = new PublicApiResponseCacheMiddleware(
            _ => throw new InvalidOperationException(
                "Cached exact leaderboard continued to live reads."),
            NullLogger<
                PublicApiResponseCacheMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path =
            "/api/leaderboard/song/Solo_Guitar";
        context.Request.QueryString =
            new QueryString("?leeway=1");
        SetPublicationEndpoint(
            context,
            "/api/leaderboard/{songId}/{instrument}");
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(
            context,
            metaDb,
            gate,
            new PublicApiCacheTelemetry());

        Assert.Equal(
            StatusCodes.Status200OK,
            context.Response.StatusCode);
        Assert.Equal(
            "hit",
            context.Response.Headers["X-FST-Public-Cache"]);
    }

    [Fact]
    public async Task PublicApiResponseCacheMiddleware_DoesNotRegisterSelectedProfileDuringMaxScoreMaintenance()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(
            new PublicReadFreezeState(
                true,
                DateTime.UtcNow,
                1296,
                PublicReadFreezeState
                    .MaxScoreMaintenanceReasonPrefix
                + new string('e', 64)));
        var json = Encoding.UTF8.GetBytes("{\"ok\":true}");
        metaDb.GetCachedResponse(Arg.Any<string>())
            .Returns((
                json,
                ResponseCacheService.ComputeETag(json)));
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var middleware = new PublicApiResponseCacheMiddleware(
            _ => Task.CompletedTask,
            NullLogger<
                PublicApiResponseCacheMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path =
            "/api/leaderboard/song/Solo_Guitar";
        context.Request.Headers[
            SelectedProfileHeaders.SelectedProfileTypeHeader] =
            "player";
        context.Request.Headers[
            SelectedProfileHeaders.SelectedProfileIdHeader] =
            "account-1";
        SetPublicationEndpoint(
            context,
            "/api/leaderboard/{songId}/{instrument}");
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(
            context,
            metaDb,
            gate,
            new PublicApiCacheTelemetry());

        metaDb.DidNotReceive()
            .TouchWebRegistrationActivity(
                Arg.Any<string>());
        metaDb.DidNotReceive()
            .RegisterSelectedBandActivity(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>());
    }

    [Fact]
    public async Task PublicApiResponseCacheMiddleware_ExactGenerationHitSetsPublicationContext()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(
            new PublicReadFreezeState(
                true,
                DateTime.UtcNow,
                1278,
                PublicReadFreezeState.PublicationCommitIntentReason));
        var json = Encoding.UTF8.GetBytes("{\"publicationId\":19}");
        metaDb.GetCurrentCacheLookup(Arg.Any<string>()).Returns(
            new PublicationCacheLookup(
                true,
                new PublicationCachedResponse(
                    19,
                    1278,
                    DateTime.UtcNow,
                    json,
                    ResponseCacheService.ComputeETag(json))));
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var middleware = new PublicApiResponseCacheMiddleware(
            _ => throw new InvalidOperationException(
                "Exact generation cache hit continued to the lock boundary."),
            NullLogger<PublicApiResponseCacheMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/rankings/Solo_Guitar";
        context.Request.Headers[
            SelectedProfileHeaders.SelectedProfileTypeHeader] =
            "player";
        context.Request.Headers[
            SelectedProfileHeaders.SelectedProfileIdHeader] =
            "account-1";
        context.Request.Headers[
            SelectedProfileHeaders.LegacySelectedPlayerHeader] =
            "account-1";
        SetPublicationEndpoint(
            context,
            "/api/rankings/{instrument}");
        var registrationLease =
            Substitute.For<IRegistrationMutationLease>();
        metaDb.AcquireRegistrationMutationLeaseAsync(
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(registrationLease));
        var registrationMutations =
            new RegistrationMutationCoordinator(
                metaDb,
                Substitute.For<IPathDataStore>(),
                Substitute.For<
                    ISongInstrumentSupportCache>());
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .AddSingleton(registrationMutations)
            .BuildServiceProvider();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(
            context,
            metaDb,
            gate,
            new PublicApiCacheTelemetry());

        Assert.Equal(
            19,
            context.GetPublicationReadContext()?.PublicationId);
        Assert.Equal(
            "19",
            context.Response.Headers[
                PublicationReadContextMiddleware.PublicationHeader]);
        Assert.Equal(
            "hit",
            context.Response.Headers["X-FST-Public-Cache"]);
        metaDb.DidNotReceive().GetCachedResponse(
            Arg.Any<string>());
        metaDb.Received(1)
            .TouchWebRegistrationActivity("account-1");
    }

    [Fact]
    public async Task PublicReadGate_TriggersSingleFlightRecoveryWithoutBlockingRequests()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(
            new PublicReadFreezeState(
                true,
                DateTime.UtcNow.AddMinutes(-5),
                1278,
                PublicReadFreezeState
                    .PublicationCommitIntentReason),
            new PublicReadFreezeState(
                true,
                DateTime.UtcNow.AddMinutes(-5),
                1278,
                PublicReadFreezeState
                    .PublicationCommitIntentReason));
        var recovery =
            Substitute.For<IPublicationRecoveryCoordinator>();
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance,
            Options.Create(new PublicationCommitOptions
            {
                StaleCommitIntentSeconds = 1,
            }),
            recovery);

        var stopwatch =
            System.Diagnostics.Stopwatch.StartNew();
        var states = await Task.WhenAll(
            Enumerable.Range(0, 100)
                .Select(_ => Task.Run(gate.GetState)));
        stopwatch.Stop();

        Assert.All(states, state => Assert.True(state.IsFrozen));
        recovery.Received(1).Trigger();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        _ = metaDb.DidNotReceive()
            .ReconcileStalePublicationCommitIntent(
                Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task PublicReadGateRecoveryClearsPendingIsolationForCurrentPublication()
    {
        using var fixture = new InMemoryMetaDatabase();
        var metaDb = fixture.Db;
        var publishedScrapeId = metaDb.StartScrapeRun();
        metaDb.CompleteScrapeRun(
            publishedScrapeId,
            1,
            1,
            1,
            1);
        metaDb.PublishScrapeRun(
            publishedScrapeId,
            promoteCachedResponses: false);
        var publicationId =
            metaDb.GetPublicationPointerState()
                .CurrentPublicationId;
        metaDb.SetPublicReadFreeze(
            true,
            publishedScrapeId,
            PublicReadFreezeState
                .PublicationFailureIsolationPendingReason);
        var options = Options.Create(
            new PublicationCommitOptions
            {
                StaleCommitIntentSeconds = 1,
            });
        var coordinator =
            new PublicationRecoveryCoordinator(
                metaDb,
                options,
                Options.Create(new ScraperOptions()),
                NullLogger<PublicationRecoveryCoordinator>.Instance);
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance,
            options,
            coordinator);

        Assert.True(gate.GetState().IsFrozen);
        await WaitUntilAsync(
            () => !metaDb.GetPublicReadFreezeState().IsFrozen,
            TimeSpan.FromSeconds(5));
        gate.Invalidate();

        Assert.False(gate.GetState().IsFrozen);
        Assert.Equal(
            publicationId,
            metaDb.GetPublicationPointerState()
                .CurrentPublicationId);
        Assert.Equal(
            PublicationGenerationStatus.Current,
            metaDb.GetPublicationGeneration(
                publicationId!.Value)?.Status);
    }

    [Fact]
    public async Task PinnedCacheMissNeverFallsBackToLegacyCache()
    {
        using var fixture = new InMemoryMetaDatabase();
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(
            new PublicReadFreezeState(
                true,
                DateTime.UtcNow,
                1278,
                "scrape"));
        metaDb.GetCurrentCacheLookup(Arg.Any<string>())
            .Returns(new PublicationCacheLookup(
                HasCurrentPublication: false,
                CachedResponse: null));
        metaDb.GetCachedResponse(Arg.Any<string>())
            .Returns((
                Encoding.UTF8.GetBytes("{\"legacy\":true}"),
                "\"legacy\""));
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var publicationService =
            new PublicationReadContextService(
                metaDb,
                fixture.DataSource,
                Options.Create(new FeatureOptions
                {
                    EnablePublicationReadContext = true,
                }));
        var nextCalled = false;
        var middleware = new PublicApiResponseCacheMiddleware(
            context =>
            {
                nextCalled = true;
                context.Response.StatusCode =
                    StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            NullLogger<PublicApiResponseCacheMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path =
            "/api/rankings/Solo_Guitar";
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        SetPublicationEndpoint(
            context,
            "/api/rankings/{instrument}");

        await middleware.InvokeAsync(
            context,
            metaDb,
            gate,
            new PublicApiCacheTelemetry(),
            publicationService);

        Assert.True(nextCalled);
        Assert.Equal(
            StatusCodes.Status204NoContent,
            context.Response.StatusCode);
        metaDb.DidNotReceive()
            .GetCachedResponse(Arg.Any<string>());
    }

    [Fact]
    public void PublicationCommitIntentLeaseRestoresPreviousState()
    {
        using var fixture = new InMemoryMetaDatabase();
        var metaDb = Substitute.For<IMetaDatabase>();
        var previousState = new PublicReadFreezeState(
            true,
            DateTime.UtcNow.AddMinutes(-1),
            1278,
            "publish");
        metaDb.GetPublicReadFreezeState()
            .Returns(previousState);
        var commitIntent = new PublicationCommitIntentHandle(
            1278,
            "test-owner",
            DateTime.UtcNow);
        metaDb.BeginPublicationCommitIntent(1278)
            .Returns(commitIntent);
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var publicationService =
            new PublicationReadContextService(
                metaDb,
                fixture.DataSource,
                Options.Create(new FeatureOptions()));
        using var cache = new ResponseCacheService(
            TimeSpan.FromMinutes(1),
            gate);
        var lifecycle = new ScrapeLifecycleNotifier(
            cache,
            cache,
            cache,
            cache,
            cache,
            metaDb,
            gate,
            publicationService,
            NullLogger<ScrapeLifecycleNotifier>.Instance);

        using (lifecycle.PublicationCommitStarting(1278))
        {
            _ = metaDb.Received(1)
                .BeginPublicationCommitIntent(1278);
        }

        metaDb.Received(1).RestorePublicationCommitIntent(
            commitIntent,
            previousState);
    }

    [Fact]
    public void PublicationCommitDeferredWriteFailureKeepsLocalGateFailClosed()
    {
        using var fixture = new InMemoryMetaDatabase();
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.When(item => item.SetPublicReadFreeze(
                true,
                1278,
                PublicReadFreezeState
                    .PublicationCommitDeferredReason))
            .Do(_ => throw new NpgsqlException(
                "injected transient write failure"));
        metaDb.GetPublicReadFreezeState()
            .Returns(new PublicReadFreezeState(
                true,
                DateTime.UtcNow,
                1278,
                "publish"));
        var recovery =
            Substitute.For<IPublicationRecoveryCoordinator>();
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance,
            Options.Create(new PublicationCommitOptions()),
            recovery);
        var publicationService =
            new PublicationReadContextService(
                metaDb,
                fixture.DataSource,
                Options.Create(new FeatureOptions()));
        using var cache = new ResponseCacheService(
            TimeSpan.FromMinutes(1),
            gate);
        var lifecycle = new ScrapeLifecycleNotifier(
            cache,
            cache,
            cache,
            cache,
            cache,
            metaDb,
            gate,
            publicationService,
            NullLogger<ScrapeLifecycleNotifier>.Instance);

        var exception = Record.Exception(() =>
            lifecycle.PublicationCommitDeferred(1278));

        Assert.Null(exception);
        Assert.True(gate.RequiresCachedReads);
        Assert.True(gate.GetState().IsFrozen);
        Assert.Equal(
            PublicReadFreezeState
                .PublicationFailureIsolationPendingReason,
            gate.GetState().Reason);
        recovery.Received().Trigger();
    }

    [Fact]
    public async Task DeferredTransitionFailureKeepsDurableCommitLatchVisibleToSeparateApi()
    {
        using var fixture = new InMemoryMetaDatabase();
        var metaDb = fixture.Db;
        var publishedScrapeId = metaDb.StartScrapeRun();
        metaDb.CompleteScrapeRun(
            publishedScrapeId,
            1,
            1,
            1,
            1);
        metaDb.PublishScrapeRun(
            publishedScrapeId,
            promoteCachedResponses: false);
        var candidateScrapeId = metaDb.StartScrapeRun();
        metaDb.CompleteScrapeRun(
            candidateScrapeId,
            1,
            2,
            2,
            2);
        _ = metaDb.PrepareScrapePublication(
            candidateScrapeId,
            promoteCachedResponses: false);
        var workerGate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var apiGate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var publicationService =
            new PublicationReadContextService(
                metaDb,
                fixture.DataSource,
                Options.Create(new FeatureOptions()));
        using var cache = new ResponseCacheService(
            TimeSpan.FromMinutes(1),
            workerGate);
        var lifecycle = new ScrapeLifecycleNotifier(
            cache,
            cache,
            cache,
            cache,
            cache,
            metaDb,
            workerGate,
            publicationService,
            NullLogger<ScrapeLifecycleNotifier>.Instance);
        var allowTransition = false;
        metaDb.DeferredTransitionTestHook = () =>
        {
            if (!Volatile.Read(ref allowTransition))
            {
                throw new NpgsqlException(
                    "injected deferred transition failure");
            }
        };

        using (var intent =
               lifecycle.PublicationCommitStarting(
                   candidateScrapeId))
        {
            intent.Defer();
        }

        var durableState = metaDb.GetPublicReadFreezeState();
        Assert.True(durableState.PublicationCommitPending);
        Assert.True(apiGate.GetState().PublicationCommitPending);
        Assert.True(apiGate.RequiresCachedReads);
        Assert.True(workerGate.RequiresCachedReads);

        Volatile.Write(ref allowTransition, true);
        await WaitUntilAsync(
            () => metaDb.GetPublicReadFreezeState()
                .PublicationCommitDeferred,
            TimeSpan.FromSeconds(5));
        apiGate.Invalidate();
        Assert.True(apiGate.GetState().PublicationCommitDeferred);

        metaDb.DeferredTransitionTestHook = null;
        metaDb.FailScrapeRun(
            candidateScrapeId,
            "test",
            "cleanup");
    }

    [Fact]
    public async Task IsolationRecordingAndPendingWriteFailuresKeepCrossProcessCommitLatch()
    {
        using var fixture = new InMemoryMetaDatabase();
        var metaDb = fixture.Db;
        var publishedScrapeId = metaDb.StartScrapeRun();
        metaDb.CompleteScrapeRun(
            publishedScrapeId,
            1,
            1,
            1,
            1);
        metaDb.PublishScrapeRun(
            publishedScrapeId,
            promoteCachedResponses: false);
        var candidateScrapeId = metaDb.StartScrapeRun();
        metaDb.CompleteScrapeRun(
            candidateScrapeId,
            1,
            2,
            2,
            2);
        _ = metaDb.PrepareScrapePublication(
            candidateScrapeId,
            promoteCachedResponses: false);
        metaDb.FailureIsolationTestHook =
            () => throw new NpgsqlException(
                "injected failure-record error");
        metaDb.IsolationPendingTransitionTestHook =
            () => throw new NpgsqlException(
                "injected pending-transition error");

        Assert.Throws<NpgsqlException>(() =>
            metaDb.FailScrapeRun(
                candidateScrapeId,
                "publication",
                "injected"));
        Assert.True(
            metaDb.GetPublicReadFreezeState()
                .PublicationCommitPending);

        var workerGate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var apiGate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var publicationService =
            new PublicationReadContextService(
                metaDb,
                fixture.DataSource,
                Options.Create(new FeatureOptions()));
        using var cache = new ResponseCacheService(
            TimeSpan.FromMinutes(1),
            workerGate);
        var lifecycle = new ScrapeLifecycleNotifier(
            cache,
            cache,
            cache,
            cache,
            cache,
            metaDb,
            workerGate,
            publicationService,
            NullLogger<ScrapeLifecycleNotifier>.Instance);
        metaDb.PublicReadFreezeWriteTestHook =
            () => throw new NpgsqlException(
                "injected pending-freeze write error");

        lifecycle.ScrapeFailureIsolationPending(
            candidateScrapeId);

        Assert.True(workerGate.RequiresCachedReads);
        Assert.True(
            metaDb.GetPublicReadFreezeState()
                .PublicationCommitPending);
        Assert.True(apiGate.GetState().PublicationCommitPending);
        Assert.True(apiGate.RequiresCachedReads);
        var nextCalled = false;
        var middleware =
            new PublicationBoundaryReadLeaseMiddleware(
                _ =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path =
            "/api/rankings/Solo_Guitar";
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        SetPublicationEndpoint(
            context,
            "/api/rankings/{instrument}");
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(
            context,
            publicationService,
            apiGate);

        Assert.False(nextCalled);
        Assert.Equal(
            StatusCodes.Status503ServiceUnavailable,
            context.Response.StatusCode);

        metaDb.FailureIsolationTestHook = null;
        metaDb.IsolationPendingTransitionTestHook = null;
        metaDb.PublicReadFreezeWriteTestHook = null;
        using (var connection =
               fixture.DataSource.OpenConnection())
        using (var stale = connection.CreateCommand())
        {
            stale.CommandText = """
                UPDATE scrape_publication_state
                SET publication_commit_intent_heartbeat_at =
                    now() - interval '5 minutes'
                WHERE id = TRUE
                """;
            stale.ExecuteNonQuery();
        }
        _ = metaDb.ReconcileStalePublicationCommitIntent(
            TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void FailedIsolationRecordingFailureKeepsReadsClosedUntilReconciled()
    {
        using var fixture = new InMemoryMetaDatabase();
        var metaDb = fixture.Db;
        var publishedScrapeId = metaDb.StartScrapeRun();
        metaDb.CompleteScrapeRun(
            publishedScrapeId,
            1,
            1,
            1,
            1);
        metaDb.PublishScrapeRun(
            publishedScrapeId,
            promoteCachedResponses: false);
        var candidateScrapeId = metaDb.StartScrapeRun();
        metaDb.CompleteScrapeRun(
            candidateScrapeId,
            1,
            2,
            2,
            2);
        metaDb.SetPublicReadFreeze(
            true,
            candidateScrapeId,
            "publish");
        metaDb.FailureIsolationTestHook =
            () => throw new InvalidOperationException(
                "injected isolation persistence failure");
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var publicationService =
            new PublicationReadContextService(
                metaDb,
                fixture.DataSource,
                Options.Create(new FeatureOptions()));
        using var cache = new ResponseCacheService(
            TimeSpan.FromMinutes(1),
            gate);
        var lifecycle = new ScrapeLifecycleNotifier(
            cache,
            cache,
            cache,
            cache,
            cache,
            metaDb,
            gate,
            publicationService,
            NullLogger<ScrapeLifecycleNotifier>.Instance);

        Assert.Throws<InvalidOperationException>(() =>
            metaDb.FailScrapeRun(
                candidateScrapeId,
                "publication",
                "injected"));
        lifecycle.ScrapeFailureIsolationPending(
            candidateScrapeId);
        lifecycle.ScrapeFailed(
            durableIsolationConfirmed: false);

        var pending = metaDb.GetPublicReadFreezeState();
        Assert.True(pending.IsFrozen);
        Assert.True(
            pending.PublicationFailureIsolationPending);
        Assert.NotNull(
            metaDb.GetPublicationPointerState()
                .WorkingPublicationId);

        metaDb.FailureIsolationTestHook = null;
        Assert.True(gate.RequiresCachedReads);
        Assert.True(cache.IsFrozen);
        Assert.NotNull(
            metaDb.GetPublicationPointerState()
                .WorkingPublicationId);

        _ = metaDb.ReconcileStalePublicationCommitIntent(
            TimeSpan.FromSeconds(30));

        Assert.Null(
            metaDb.GetPublicationPointerState()
                .WorkingPublicationId);
        Assert.False(
            metaDb.GetPublicReadFreezeState().IsFrozen);
        Assert.True(
            metaDb.GetFailedCandidateReadIsolationState()
                .IsFrozen);
    }

    [Fact]
    public async Task PublicApiResponseCacheMiddleware_UsesPinnedPublicationCache()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(
            new PublicReadFreezeState(true, DateTime.UtcNow, 793, "test"));
        var json = Encoding.UTF8.GetBytes("{\"publicationId\":42}");
        metaDb.GetCachedResponse(42, Arg.Any<string>())
            .Returns((json, ResponseCacheService.ComputeETag(json)));
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var middleware = new PublicApiResponseCacheMiddleware(
            _ => throw new InvalidOperationException("Pinned cache miss."),
            NullLogger<PublicApiResponseCacheMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/rankings/Solo_Guitar";
        SetPublicationEndpoint(context, "/api/rankings/{instrument}");
        context.SetPublicationReadContext(
            new PublicationReadContext(42, 793, DateTime.UtcNow));
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(
            context,
            metaDb,
            gate,
            new PublicApiCacheTelemetry());

        _ = metaDb.Received(1).GetCachedResponse(
            42,
            Arg.Any<string>());
        metaDb.DidNotReceive().GetCachedResponse(Arg.Any<string>());
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
    public async Task PublicApiResponseCacheMiddleware_DelegatesMarkedFailedCandidateReadToEndpoint()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicReadFreezeState().Returns(
            PublicReadFreezeState.NotFrozen);
        metaDb.GetFailedCandidateReadIsolationState().Returns(
            new PublicReadFreezeState(
                true,
                DateTime.UtcNow,
                1263,
                MetaDatabase.FailedCandidateReadIsolationReason));
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var nextCalled = false;
        var middleware = new PublicApiResponseCacheMiddleware(context =>
        {
            nextCalled = true;
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        }, NullLogger<PublicApiResponseCacheMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/player/account";
        SetPublicationEndpoint(
            context,
            "/api/player/{accountId}",
            handlesFailedCandidateRead: true);
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        var telemetry = new PublicApiCacheTelemetry();

        await middleware.InvokeAsync(context, metaDb, gate, telemetry);

        Assert.True(nextCalled);
        Assert.Equal(
            StatusCodes.Status204NoContent,
            context.Response.StatusCode);
        Assert.Equal(
            "endpoint",
            context.Response.Headers["X-FST-Public-Cache"]);
        Assert.Equal(1, telemetry.Snapshot().Bypassed);
        metaDb.DidNotReceive().GetCachedResponse(Arg.Any<string>());
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

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
                return;
            await Task.Delay(50);
        }

        throw new TimeoutException(
            "Condition was not satisfied before timeout.");
    }

    private static void SetPublicationEndpoint(
        HttpContext context,
        string routePattern,
        string method = "GET",
        bool handlesFailedCandidateRead = false)
    {
        var metadata = handlesFailedCandidateRead
            ? new EndpointMetadataCollection(
                new HttpMethodMetadata([method]),
                PublicationBound.Instance,
                EndpointHandlesFailedCandidateRead.Instance)
            : new EndpointMetadataCollection(
                new HttpMethodMetadata([method]),
                PublicationBound.Instance);
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(routePattern),
            order: 0,
            metadata,
            displayName: routePattern));
    }
}
