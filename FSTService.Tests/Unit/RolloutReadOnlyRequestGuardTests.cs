using FSTService.Api;
using FSTService.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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
