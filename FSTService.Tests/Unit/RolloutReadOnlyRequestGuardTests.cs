using FSTService.Api;
using FSTService.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class RolloutReadOnlyRequestGuardTests
{
    [Fact]
    public async Task SelectedProfileGet_ReadOnlyMode_DoesNotPersistActivity()
    {
        var metaDatabase = Substitute.For<IMetaDatabase>();
        var context = CreateContext(metaDatabase, HttpMethods.Get, "/api/version");
        context.Request.Headers["X-FST-Selected-Player"] = "account";
        var middleware = new SelectedProfileActivityMiddleware(
            next: ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            Options.Create(new ScraperOptions
            {
                RolloutReadOnlyStartup = true,
            }));

        await middleware.InvokeAsync(context);

        metaDatabase.DidNotReceive()
            .TouchWebRegistrationActivity(Arg.Any<string>());
    }

    [Fact]
    public async Task SelectedProfileGet_NormalMode_PreservesActivityPersistence()
    {
        var metaDatabase = Substitute.For<IMetaDatabase>();
        var context = CreateContext(metaDatabase, HttpMethods.Get, "/api/version");
        context.Request.Headers["X-FST-Selected-Player"] = "account";
        var middleware = new SelectedProfileActivityMiddleware(
            next: ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            Options.Create(new ScraperOptions()));

        await middleware.InvokeAsync(context);

        metaDatabase.Received(1).TouchWebRegistrationActivity("account");
    }

    [Fact]
    public async Task SelectedProfiles_DoNotChangeRegistrationsDuringMaxScoreResumeFreeze()
    {
        var metaDatabase = Substitute.For<IMetaDatabase>();
        metaDatabase.GetPublicReadFreezeState().Returns(
            new PublicReadFreezeState(
                true,
                DateTime.UtcNow,
                1296,
                PublicReadFreezeState
                    .MaxScoreMaintenanceReasonPrefix
                + new string('a', 64)));
        var gate = new PublicReadGateService(
            metaDatabase,
            NullLogger<PublicReadGateService>.Instance);
        var services = new ServiceCollection()
            .AddSingleton(metaDatabase)
            .AddSingleton(gate)
            .BuildServiceProvider();
        var middleware = new SelectedProfileActivityMiddleware(
            next: context =>
            {
                context.Response.StatusCode =
                    StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            Options.Create(new ScraperOptions()));

        var playerContext = new DefaultHttpContext
        {
            RequestServices = services,
        };
        playerContext.Request.Path = "/api/version";
        playerContext.Request.Headers[
            SelectedProfileHeaders.LegacySelectedPlayerHeader] =
            "account";
        await middleware.InvokeAsync(playerContext);

        var bandContext = new DefaultHttpContext
        {
            RequestServices = services,
        };
        bandContext.Request.Path = "/api/version";
        bandContext.Request.Headers[
            SelectedProfileHeaders.SelectedProfileTypeHeader] =
            "band";
        bandContext.Request.Headers[
            SelectedProfileHeaders.SelectedBandIdHeader] =
            "band-id";
        bandContext.Request.Headers[
            SelectedProfileHeaders.SelectedBandTypeHeader] =
            "Band_Duets";
        bandContext.Request.Headers[
            SelectedProfileHeaders.SelectedBandTeamKeyHeader] =
            "account:mate";
        await middleware.InvokeAsync(bandContext);

        metaDatabase.DidNotReceive()
            .TouchWebRegistrationActivity(
                Arg.Any<string>());
        metaDatabase.DidNotReceive()
            .RegisterSelectedBandActivity(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>());
    }

    [Theory]
    [InlineData("POST", "/api/player/account/track")]
    [InlineData("PUT", "/api/admin/value")]
    [InlineData("DELETE", "/api/admin/value")]
    [InlineData("GET", "/api/player/account/stats")]
    [InlineData("GET", "/api/player/account/stats/")]
    [InlineData("GET", "/API/PLAYER/account/STATS///")]
    [InlineData("GET", "/api/bands/Duets/team/sync-status")]
    [InlineData("GET", "/api/bands/Duets/team/sync-status/")]
    [InlineData("GET", "/API/BANDS/Duets/team/SYNC-STATUS///")]
    [InlineData("GET", "/api/admin/epic-token")]
    [InlineData("GET", "/api/admin/epic-token/")]
    [InlineData("GET", "/API/ADMIN/EPIC-TOKEN///")]
    [InlineData("HEAD", "/api/admin/epic-token/")]
    public async Task MutationCapableRequests_ReadOnlyMode_Return503BeforeHandler(
        string method,
        string path)
    {
        var handlerCalled = false;
        var context = CreateContext(
            Substitute.For<IMetaDatabase>(),
            method,
            path);
        context.Response.Body = new MemoryStream();
        var middleware = new RolloutReadOnlyRequestGuardMiddleware(
            next: _ =>
            {
                handlerCalled = true;
                return Task.CompletedTask;
            },
            Options.Create(new ScraperOptions
            {
                RolloutReadOnlyStartup = true,
            }),
            new RolloutReadOnlyViolationMonitor());

        await middleware.InvokeAsync(context);

        Assert.False(handlerCalled);
        Assert.Equal(
            StatusCodes.Status503ServiceUnavailable,
            context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("/api/admin/epic-token/", "/api/admin/epic-token")]
    [InlineData("/api/admin/epic-token///", "/api/admin/epic-token")]
    public void CanonicalizePath_TrimsTrailingSlashExceptRoot(
        string? path,
        string expected)
    {
        Assert.Equal(
            expected,
            RolloutReadOnlyRequestGuardMiddleware.CanonicalizePath(path));
    }

    [Fact]
    public async Task MutationRequest_NormalMode_ReachesHandler()
    {
        var handlerCalled = false;
        var context = CreateContext(
            Substitute.For<IMetaDatabase>(),
            HttpMethods.Post,
            "/api/player/account/track");
        var middleware = new RolloutReadOnlyRequestGuardMiddleware(
            next: _ =>
            {
                handlerCalled = true;
                return Task.CompletedTask;
            },
            Options.Create(new ScraperOptions()),
            new RolloutReadOnlyViolationMonitor());

        await middleware.InvokeAsync(context);

        Assert.True(handlerCalled);
    }

    [Fact]
    public async Task NestedReadOnlyViolation_ReadOnlyMode_Returns503AndLatchesHealth()
    {
        var violation = new PostgresException(
            "cannot execute CREATE TABLE in a read-only transaction",
            "ERROR",
            "ERROR",
            "25006");
        var monitor = new RolloutReadOnlyViolationMonitor();
        var context = CreateContext(
            Substitute.For<IMetaDatabase>(),
            HttpMethods.Get,
            "/api/player/account");
        context.Response.Body = new MemoryStream();
        var middleware = new RolloutReadOnlyRequestGuardMiddleware(
            next: _ => Task.FromException(
                new AggregateException(
                    new InvalidOperationException(
                        "parallel filtered read failed",
                        violation))),
            Options.Create(new ScraperOptions
            {
                RolloutReadOnlyStartup = true,
            }),
            monitor);

        await middleware.InvokeAsync(context);

        Assert.Equal(
            StatusCodes.Status503ServiceUnavailable,
            context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.True(monitor.HasViolation);
        Assert.Same(violation, monitor.LastViolation);
    }

    private static DefaultHttpContext CreateContext(
        IMetaDatabase metaDatabase,
        string method,
        string path)
    {
        var services = new ServiceCollection()
            .AddSingleton(metaDatabase)
            .BuildServiceProvider();
        return new DefaultHttpContext
        {
            RequestServices = services,
            Request =
            {
                Method = method,
                Path = path,
            },
        };
    }
}
