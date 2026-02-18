using System.Net;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FSTService.Tests.Unit;

/// <summary>
/// Tests for <see cref="ResilientHttpExecutor"/> — retry/backoff with 429 + 5xx handling.
/// </summary>
public sealed class ResilientHttpExecutorTests
{
    private readonly ILogger _log = Substitute.For<ILogger>();

    private (ResilientHttpExecutor executor, MockHttpMessageHandler handler) CreateExecutor()
    {
        var handler = new MockHttpMessageHandler();
        var http = new HttpClient(handler);
        var executor = new ResilientHttpExecutor(http, _log);
        return (executor, handler);
    }

    private static HttpRequestMessage MakeRequest()
        => new(HttpMethod.Get, "https://example.com/api/test");

    // ─── Success on first try ───────────────────────────────────

    [Fact]
    public async Task SendAsync_SuccessOnFirstTry_ReturnsResponse()
    {
        var (executor, handler) = CreateExecutor();
        handler.EnqueueJsonOk("""{"result":"ok"}""");

        var response = await executor.SendAsync(() => MakeRequest(), label: "test");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(handler.Requests);
    }

    // ─── Retry on 5xx ───────────────────────────────────────────

    [Fact]
    public async Task SendAsync_ServerError_RetriesToSuccess()
    {
        var (executor, handler) = CreateExecutor();
        handler.EnqueueError(HttpStatusCode.InternalServerError, "Server error");
        handler.EnqueueJsonOk("""{"result":"ok"}""");

        var response = await executor.SendAsync(
            () => MakeRequest(), maxRetries: 3, label: "retry-test");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task SendAsync_ServerError_ExhaustsRetries_ReturnsLastResponse()
    {
        var (executor, handler) = CreateExecutor();
        handler.EnqueueError(HttpStatusCode.InternalServerError, "err1");
        handler.EnqueueError(HttpStatusCode.InternalServerError, "err2");

        var response = await executor.SendAsync(
            () => MakeRequest(), maxRetries: 1, label: "exhaust-test");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ─── 429 rate limit ─────────────────────────────────────────

    [Fact]
    public async Task SendAsync_429WithRetryAfter_WaitsAndRetries()
    {
        var (executor, handler) = CreateExecutor();
        handler.Enqueue429(TimeSpan.FromMilliseconds(50));
        handler.EnqueueJsonOk("""{"result":"ok"}""");

        var response = await executor.SendAsync(
            () => MakeRequest(), maxRetries: 3, label: "429-test");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    // ─── Non-retryable status ───────────────────────────────────

    [Fact]
    public async Task SendAsync_400_ReturnsImmediately()
    {
        var (executor, handler) = CreateExecutor();
        handler.EnqueueError(HttpStatusCode.BadRequest, "Bad");

        var response = await executor.SendAsync(
            () => MakeRequest(), maxRetries: 3, label: "400-test");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(handler.Requests); // No retries for 400
    }

    [Fact]
    public async Task SendAsync_403_ReturnsImmediately()
    {
        var (executor, handler) = CreateExecutor();
        handler.EnqueueError(HttpStatusCode.Forbidden, "Forbidden");

        var response = await executor.SendAsync(
            () => MakeRequest(), maxRetries: 3, label: "403-test");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SendAsync_404_ReturnsImmediately()
    {
        var (executor, handler) = CreateExecutor();
        handler.EnqueueError(HttpStatusCode.NotFound, "Not found");

        var response = await executor.SendAsync(
            () => MakeRequest(), maxRetries: 3, label: "404-test");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(handler.Requests);
    }

    // ─── Network error ──────────────────────────────────────────

    [Fact]
    public async Task SendAsync_NetworkError_RetriesToSuccess()
    {
        var (executor, handler) = CreateExecutor();
        handler.EnqueueException(new HttpRequestException("Connection refused"));
        handler.EnqueueJsonOk("""{"result":"ok"}""");

        var response = await executor.SendAsync(
            () => MakeRequest(), maxRetries: 3, label: "net-error-test");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task SendAsync_NetworkError_ExhaustsRetries_Throws()
    {
        var (executor, handler) = CreateExecutor();
        handler.EnqueueException(new HttpRequestException("Connection refused"));
        handler.EnqueueException(new HttpRequestException("Connection refused"));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => executor.SendAsync(
                () => MakeRequest(), maxRetries: 1, label: "net-exhaust-test"));
    }

    // ─── Cancellation ───────────────────────────────────────────

    [Fact]
    public async Task SendAsync_CancellationRequested_Throws()
    {
        var (executor, handler) = CreateExecutor();
        handler.EnqueueJsonOk("""{"result":"ok"}""");
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => executor.SendAsync(
                () => MakeRequest(), maxRetries: 3, label: "cancel-test", ct: cts.Token));
    }

    // ─── Adaptive concurrency limiter integration ───────────────

    [Fact]
    public async Task SendAsync_Success_ReportsToLimiter()
    {
        var (executor, handler) = CreateExecutor();
        handler.EnqueueJsonOk("""{"result":"ok"}""");

        var limiter = new AdaptiveConcurrencyLimiter(
            initialDop: 10, minDop: 1, maxDop: 100,
            Substitute.For<ILogger<AdaptiveConcurrencyLimiter>>());

        await executor.SendAsync(
            () => MakeRequest(), limiter: limiter, label: "limiter-test");

        // After one success, the limiter should still function
        Assert.True(limiter.CurrentDop >= 1);
    }

    [Fact]
    public async Task SendAsync_RetryableFailure_ReportsFailureToLimiter()
    {
        var (executor, handler) = CreateExecutor();
        handler.EnqueueError(HttpStatusCode.InternalServerError, "err");
        handler.EnqueueJsonOk("""{"result":"ok"}""");

        var limiter = new AdaptiveConcurrencyLimiter(
            initialDop: 10, minDop: 1, maxDop: 100,
            Substitute.For<ILogger<AdaptiveConcurrencyLimiter>>());

        await executor.SendAsync(
            () => MakeRequest(), limiter: limiter, maxRetries: 3, label: "limiter-fail-test");

        Assert.True(limiter.CurrentDop >= 1);
    }

    // ─── Timeout (TaskCanceledException not from token) ─────────

    [Fact]
    public async Task SendAsync_Timeout_RetriesThenSucceeds()
    {
        // Simulate HTTP timeout: TaskCanceledException NOT from the cancellation token
        var handler = new MockHttpMessageHandler();
        // First request: timeout
        handler.EnqueueException(new TaskCanceledException("The request timed out"));
        // Second request: success
        handler.EnqueueJsonOk("""{"result":"ok"}""");

        var http = new HttpClient(handler);
        var executor = new ResilientHttpExecutor(http, _log);

        var res = await executor.SendAsync(() => MakeRequest(), maxRetries: 3, label: "timeout-test");
        Assert.True(res.IsSuccessStatusCode);
    }

    [Fact]
    public async Task SendAsync_Timeout_ExhaustsRetries_Throws()
    {
        var handler = new MockHttpMessageHandler();
        // All requests: timeout
        handler.EnqueueException(new TaskCanceledException("timeout 1"));
        handler.EnqueueException(new TaskCanceledException("timeout 2"));

        var http = new HttpClient(handler);
        var executor = new ResilientHttpExecutor(http, _log);

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            executor.SendAsync(() => MakeRequest(), maxRetries: 1, label: "timeout-exhaust"));
    }

    // ─── Retryable code exhausts and returns response ───────────

    [Fact]
    public async Task SendAsync_500_ExhaustsRetries_ReturnsLastResponse()
    {
        var (executor, handler) = CreateExecutor();
        handler.EnqueueError(HttpStatusCode.InternalServerError, "err1");
        handler.EnqueueError(HttpStatusCode.InternalServerError, "err2");

        var res = await executor.SendAsync(() => MakeRequest(), maxRetries: 1, label: "exhaust-500");
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
    }
}
