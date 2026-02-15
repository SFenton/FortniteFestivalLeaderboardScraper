using FSTService.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FSTService.Tests.Unit;

public class PathTraversalGuardMiddlewareTests
{
    private readonly ILogger<PathTraversalGuardMiddleware> _log =
        Substitute.For<ILogger<PathTraversalGuardMiddleware>>();

    private (PathTraversalGuardMiddleware middleware, bool nextCalled) CreateMiddleware()
    {
        bool called = false;
        var next = new RequestDelegate(_ => { called = true; return Task.CompletedTask; });
        var mw = new PathTraversalGuardMiddleware(next, _log);
        return (mw, called);
    }

    private static DefaultHttpContext CreateContext(string path, string queryString = "")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Request.QueryString = new QueryString(queryString);
        return ctx;
    }

    [Theory]
    [InlineData("/api/songs")]
    [InlineData("/api/leaderboard/song1/Solo_Guitar")]
    [InlineData("/healthz")]
    [InlineData("/api/player/abc123")]
    public async Task CleanPath_CallsNext(string path)
    {
        bool nextCalled = false;
        var next = new RequestDelegate(_ => { nextCalled = true; return Task.CompletedTask; });
        var mw = new PathTraversalGuardMiddleware(next, _log);
        var ctx = CreateContext(path);

        await mw.InvokeAsync(ctx);

        Assert.True(nextCalled);
        Assert.NotEqual(400, ctx.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/../etc/passwd")]
    [InlineData("/api/..")]
    [InlineData("/../secret")]
    [InlineData("/api/%2e%2e/config")]
    [InlineData("/api/%2E%2E/config")]
    [InlineData("/api/%2e./config")]
    [InlineData("/api/%2E./config")]
    [InlineData("/api/.%2e/config")]
    [InlineData("/api/.%2E/config")]
    public async Task PathTraversal_InPath_Returns400(string path)
    {
        bool nextCalled = false;
        var next = new RequestDelegate(_ => { nextCalled = true; return Task.CompletedTask; });
        var mw = new PathTraversalGuardMiddleware(next, _log);
        var ctx = CreateContext(path);

        await mw.InvokeAsync(ctx);

        Assert.False(nextCalled);
        Assert.Equal(400, ctx.Response.StatusCode);
    }

    [Theory]
    [InlineData("?file=../../etc/passwd")]
    [InlineData("?path=..")]
    [InlineData("?x=%2e%2e")]
    public async Task PathTraversal_InQuery_Returns400(string query)
    {
        bool nextCalled = false;
        var next = new RequestDelegate(_ => { nextCalled = true; return Task.CompletedTask; });
        var mw = new PathTraversalGuardMiddleware(next, _log);
        var ctx = CreateContext("/api/safe", query);

        await mw.InvokeAsync(ctx);

        Assert.False(nextCalled);
        Assert.Equal(400, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task EmptyPathAndQuery_CallsNext()
    {
        bool nextCalled = false;
        var next = new RequestDelegate(_ => { nextCalled = true; return Task.CompletedTask; });
        var mw = new PathTraversalGuardMiddleware(next, _log);
        var ctx = CreateContext("");

        await mw.InvokeAsync(ctx);

        Assert.True(nextCalled);
    }
}
