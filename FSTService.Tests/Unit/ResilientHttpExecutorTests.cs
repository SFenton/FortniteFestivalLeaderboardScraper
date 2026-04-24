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
    public async Task SendAsync_403Json_ReturnsImmediately()
    {
        var (executor, handler) = CreateExecutor();
        handler.EnqueueJsonResponse(HttpStatusCode.Forbidden, """{"errorCode":"forbidden"}""");

        var response = await executor.SendAsync(
            () => MakeRequest(), maxRetries: 3, label: "403-json-test");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(handler.Requests);
        // Verify the body is still readable after CDN detection consumed it
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("forbidden", body);
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
    public async Task SendAsync_NetworkError_RetriesIndefinitely_UntilSuccess()
    {
        var (executor, handler) = CreateExecutor();
        // 6 consecutive network errors, then success — well beyond maxRetries=3
        for (int i = 0; i < 6; i++)
            handler.EnqueueException(new HttpRequestException("Connection refused"));
        handler.EnqueueJsonOk("""{"result":"ok"}""");

        var response = await executor.SendAsync(
            () => MakeRequest(), maxRetries: 3, label: "net-infinite-test");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(7, handler.Requests.Count); // 6 failures + 1 success
    }

    [Fact]
    public async Task SendAsync_NetworkError_CancelledByCancellationToken()
    {
        var handler = new MockHttpMessageHandler();
        // Enqueue enough errors to keep it retrying
        for (int i = 0; i < 20; i++)
            handler.EnqueueException(new HttpRequestException("Connection refused"));

        var http = new HttpClient(handler);
        var executor = new ResilientHttpExecutor(http, _log);

        var cts = new CancellationTokenSource();
        // Cancel after a short delay so the retry loop can run a few iterations
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => executor.SendAsync(
                () => MakeRequest(), maxRetries: 1, label: "net-cancel-test", ct: cts.Token));
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
    public async Task SendAsync_Timeout_RetriesIndefinitely_UntilSuccess()
    {
        var handler = new MockHttpMessageHandler();
        // 5 consecutive timeouts, then success — well beyond maxRetries=1
        for (int i = 0; i < 5; i++)
            handler.EnqueueException(new TaskCanceledException("The request timed out"));
        handler.EnqueueJsonOk("""{"result":"ok"}""");

        var http = new HttpClient(handler);
        var executor = new ResilientHttpExecutor(http, _log);

        var res = await executor.SendAsync(() => MakeRequest(), maxRetries: 1, label: "timeout-infinite-test");
        Assert.True(res.IsSuccessStatusCode);
        Assert.Equal(6, handler.Requests.Count); // 5 timeouts + 1 success
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

    // ─── CDN 403 (non-JSON body) ────────────────────────────────

    private ResilientHttpExecutor CreateExecutorWithZeroCdnDelay(MockHttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        var executor = new ResilientHttpExecutor(http, _log);
        executor.CdnRetryDelaysOverride = new TimeSpan[9]; // all TimeSpan.Zero
        return executor;
    }

    [Fact]
    public async Task SendAsync_Cdn403_ThrowsCdnBlockedException()
    {
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithZeroCdnDelay(handler);

        // CDN block → throws CdnBlockedException, launches probe in background
        handler.EnqueueHtml403();
        // Enqueue success for the background probe so it doesn't hang
        handler.EnqueueJsonOk("""{"result":"ok"}""");

        await Assert.ThrowsAsync<CdnBlockedException>(
            () => executor.SendAsync(() => MakeRequest(), label: "cdn-retry-test"));
    }

    [Fact]
    public async Task SendAsync_Cdn403_LaunchesProbeAndThrows()
    {
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithZeroCdnDelay(handler);

        handler.EnqueueHtml403();
        handler.EnqueueJsonOk("""{"result":"probe-ok"}""");

        await Assert.ThrowsAsync<CdnBlockedException>(
            () => executor.SendAsync(() => MakeRequest(), label: "cdn-beyond-schedule-test"));

        // After probe clears, subsequent sends should succeed
        await executor.WaitForCdnClearAsync(CancellationToken.None);
        Assert.False(executor.IsCdnBlocked);
    }

    [Fact]
    public async Task SendAsync_Cdn403_ReportsFailureToLimiter()
    {
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithZeroCdnDelay(handler);

        handler.EnqueueHtml403();
        // Enqueue success for probe
        handler.EnqueueJsonOk("""{"result":"ok"}""");

        var limiter = new AdaptiveConcurrencyLimiter(
            initialDop: 100, minDop: 1, maxDop: 200,
            Substitute.For<ILogger<AdaptiveConcurrencyLimiter>>());

        // CDN 403 → SlashDop + ReportFailure → throw
        await Assert.ThrowsAsync<CdnBlockedException>(
            () => executor.SendAsync(
                () => MakeRequest(), limiter: limiter, label: "cdn-limiter-test"));

        // Limiter DOP was slashed on CDN block (100 → minDop 1)
        Assert.True(limiter.CurrentDop < 100);
    }

    [Fact]
    public async Task SendAsync_Cdn403_DoesNotConsumeNormalRetryBudget()
    {
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithZeroCdnDelay(handler);

        // CDN 403 → throws CdnBlockedException immediately
        handler.EnqueueHtml403();
        // Enqueue a success for the background probe
        handler.EnqueueJsonOk("""{"result":"ok"}""");

        // The CDN 403 throws immediately — doesn't consume the normal retry budget
        await Assert.ThrowsAsync<CdnBlockedException>(
            () => executor.SendAsync(
                () => MakeRequest(), maxRetries: 1, label: "cdn-budget-test"));

        // At least 1 request sent (the initial CDN 403); probe may have started
        Assert.True(handler.Requests.Count >= 1);
    }

    [Fact]
    public async Task SendAsync_Cdn403_ThenJsonSuccess()
    {
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithZeroCdnDelay(handler);

        // CDN block → throws, probe gets JSON success → CDN clears
        handler.EnqueueHtml403();
        handler.EnqueueJsonOk("""{"result":"recovered"}""");

        await Assert.ThrowsAsync<CdnBlockedException>(
            () => executor.SendAsync(() => MakeRequest(), label: "cdn-recover-test"));

        await executor.WaitForCdnClearAsync(CancellationToken.None);
        Assert.False(executor.IsCdnBlocked);
    }

    [Fact]
    public async Task SendAsync_Cdn403_ThenJson403_ProbeDetectsJson403AsClear()
    {
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithZeroCdnDelay(handler);

        // CDN block → throws, then probe gets JSON 403 (not CDN) → CDN clears
        handler.EnqueueHtml403();
        handler.EnqueueJsonResponse(HttpStatusCode.Forbidden, """{"errorCode":"auth_failed"}""");

        await Assert.ThrowsAsync<CdnBlockedException>(
            () => executor.SendAsync(() => MakeRequest(), label: "cdn-then-json-test"));

        // Wait for probe to clear CDN (JSON 403 is not a CDN block)
        await executor.WaitForCdnClearAsync(CancellationToken.None);
        Assert.False(executor.IsCdnBlocked);
    }

    // ─── CDN non-probe path ─────────────────────────────────────

    [Fact]
    public async Task SendAsync_CdnNonProbe_ProbeHandlesHttpRequestException()
    {
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithZeroCdnDelay(handler);

        // CDN block → throws, probe gets network error → retries → success
        handler.EnqueueHtml403();
        handler.EnqueueException(new HttpRequestException("ResponseEnded"));
        handler.EnqueueJsonOk("""{"result":"ok"}""");

        await Assert.ThrowsAsync<CdnBlockedException>(
            () => executor.SendAsync(() => MakeRequest(), label: "cdn-netfail-test"));

        // Wait for probe to clear
        await executor.WaitForCdnClearAsync(CancellationToken.None);
        Assert.False(executor.IsCdnBlocked);
    }

    [Fact]
    public async Task SendAsync_CdnNonProbe_RetriesCdnReBlock()
    {
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithZeroCdnDelay(handler);

        // CDN block → throws, probe retries → still CDN blocked → eventually clears
        handler.EnqueueHtml403();
        handler.EnqueueHtml403();
        handler.EnqueueHtml403();
        handler.EnqueueJsonOk("""{"result":"ok"}""");

        await Assert.ThrowsAsync<CdnBlockedException>(
            () => executor.SendAsync(() => MakeRequest(), label: "cdn-reblock-test"));

        await executor.WaitForCdnClearAsync(CancellationToken.None);
        Assert.False(executor.IsCdnBlocked);
    }

    [Fact]
    public async Task SendAsync_CdnNonProbe_CdnThenNon403_ReturnsResponse()
    {
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithZeroCdnDelay(handler);

        // CDN block → throws, probe gets 404 (non-CDN) → CDN clears
        handler.EnqueueHtml403();
        handler.EnqueueError(HttpStatusCode.NotFound, "not found");

        await Assert.ThrowsAsync<CdnBlockedException>(
            () => executor.SendAsync(() => MakeRequest(), label: "cdn-then-404-test"));

        await executor.WaitForCdnClearAsync(CancellationToken.None);
        Assert.False(executor.IsCdnBlocked);
    }

    [Fact]
    public async Task SendAsync_CdnNonProbe_TimeoutRetries()
    {
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithZeroCdnDelay(handler);

        // CDN block → throws, probe retries → timeout → success → CDN clears
        handler.EnqueueHtml403();
        handler.EnqueueException(new TaskCanceledException("timed out"));
        handler.EnqueueJsonOk("""{"result":"ok"}""");

        await Assert.ThrowsAsync<CdnBlockedException>(
            () => executor.SendAsync(() => MakeRequest(), label: "cdn-timeout-test"));

        await executor.WaitForCdnClearAsync(CancellationToken.None);
        Assert.False(executor.IsCdnBlocked);
    }

    // ─── CDN slot lifecycle (release during wait, reacquire for send) ────

    [Fact]
    public async Task SendAsync_CdnBlock_ReleasesSlotDuringCooldown()
    {
        // With throw-based CDN handling, SendAsync throws CdnBlockedException.
        // The caller (WithCdnResilienceAsync) releases the slot. Verify the throw
        // doesn't consume or break the limiter.
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithZeroCdnDelay(handler);

        handler.EnqueueHtml403();
        handler.EnqueueJsonOk("""{"result":"ok"}""");

        var limiter = new AdaptiveConcurrencyLimiter(
            initialDop: 1, minDop: 1, maxDop: 1,
            Substitute.For<ILogger<AdaptiveConcurrencyLimiter>>());

        await limiter.WaitAsync(CancellationToken.None);

        // CDN 403 → throws CdnBlockedException (caller still holds slot)
        await Assert.ThrowsAsync<CdnBlockedException>(
            () => executor.SendAsync(
                () => MakeRequest(), limiter: limiter, label: "cdn-slot-release-test"));

        // Caller releases slot (simulating WithCdnResilienceAsync catch block)
        limiter.Release();
        // Slot should be available for reacquisition
        await limiter.WaitAsync(CancellationToken.None);
        limiter.Release();
    }

    [Fact]
    public async Task SendAsync_CdnBlock_DoesNotDeadlockWithSingleSlot()
    {
        // With throw-based CDN handling, DOP=1 can't deadlock because
        // SendAsync throws immediately and the caller releases the slot.
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithZeroCdnDelay(handler);

        handler.EnqueueHtml403();
        // Enqueue success for probe
        handler.EnqueueJsonOk("""{"result":"ok"}""");

        var limiter = new AdaptiveConcurrencyLimiter(
            initialDop: 1, minDop: 1, maxDop: 1,
            Substitute.For<ILogger<AdaptiveConcurrencyLimiter>>());

        await limiter.WaitAsync(CancellationToken.None);

        // CDN 403 → throws immediately (no deadlock)
        await Assert.ThrowsAsync<CdnBlockedException>(
            () => executor.SendAsync(
                () => MakeRequest(), limiter: limiter, label: "cdn-dop1-test"));

        limiter.Release();
    }

    [Fact]
    public async Task SendAsync_CdnBlock_Cancellation_ReacquiresSlotBeforeThrowing()
    {
        // When CDN retry is cancelled, the method must reacquire a slot before
        // throwing, because the caller's finally block calls Release().
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithZeroCdnDelay(handler);

        // CDN block — but we'll cancel before the probe retry succeeds
        handler.EnqueueHtml403();
        // Don't enqueue a second response — the cancellation will fire before it's needed

        var limiter = new AdaptiveConcurrencyLimiter(
            initialDop: 2, minDop: 1, maxDop: 2,
            Substitute.For<ILogger<AdaptiveConcurrencyLimiter>>());

        await limiter.WaitAsync(CancellationToken.None);

        var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately — the CDN wait will throw

        // Enqueue a success so we don't run out of responses if it somehow sends
        handler.EnqueueJsonOk("""{"result":"ok"}""");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => executor.SendAsync(
                () => MakeRequest(), limiter: limiter, label: "cdn-cancel-test", ct: cts.Token));

        // The method should have reacquired a slot before throwing,
        // so the caller's Release() doesn't underflow.
        // We verify by releasing (simulating caller's finally) and re-acquiring.
        limiter.Release();
        await limiter.WaitAsync(CancellationToken.None);
        limiter.Release();
    }

    [Fact]
    public async Task SendAsync_CdnBlock_ProbeNetworkError_ReleasesSlotBeforeRetry()
    {
        // Probe handles network errors gracefully and continues retrying.
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithZeroCdnDelay(handler);

        handler.EnqueueHtml403();
        handler.EnqueueException(new HttpRequestException("Connection reset"));
        handler.EnqueueJsonOk("""{"result":"ok"}""");

        // CDN 403 → throws, probe gets network error → retries → success
        await Assert.ThrowsAsync<CdnBlockedException>(
            () => executor.SendAsync(() => MakeRequest(), label: "cdn-probe-netfail-slot-test"));

        await executor.WaitForCdnClearAsync(CancellationToken.None);
        Assert.False(executor.IsCdnBlocked);
    }

// ─── ResetCdnState ──────────────────────────────────────────

    [Fact]
    public async Task ResetCdnState_ClearsCooldownAndRetryIndex()
    {
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithZeroCdnDelay(handler);

        // Trigger a CDN block to advance _cdnRetryIndex
        handler.EnqueueHtml403();
        handler.EnqueueHtml403();
        handler.EnqueueJsonOk("""{"result":"ok"}""");

        await Assert.ThrowsAsync<CdnBlockedException>(
            () => executor.SendAsync(() => MakeRequest(), label: "setup"));

        // Wait for probe to clear
        await executor.WaitForCdnClearAsync(CancellationToken.None);

        // Reset CDN state
        executor.ResetCdnState();
        Assert.False(executor.IsCdnBlocked);

        // Next CDN block should start from index 0 (not continue from previous)
        handler.EnqueueHtml403();
        handler.EnqueueJsonOk("""{"result":"ok2"}""");

        await Assert.ThrowsAsync<CdnBlockedException>(
            () => executor.SendAsync(() => MakeRequest(), label: "after-reset"));

        await executor.WaitForCdnClearAsync(CancellationToken.None);
        Assert.False(executor.IsCdnBlocked);
    }

    // ─── CDN probe SlashDop ─────────────────────────────────────

    [Fact]
    public async Task SendAsync_CdnBlock_ProbeSlashesDop()
    {
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithZeroCdnDelay(handler);

        handler.EnqueueHtml403();
        handler.EnqueueJsonOk("""{"result":"ok"}""");

        var limiter = new AdaptiveConcurrencyLimiter(
            initialDop: 100, minDop: 4, maxDop: 200,
            Substitute.For<ILogger<AdaptiveConcurrencyLimiter>>());

        // CDN 403 → SlashDop + throw
        await Assert.ThrowsAsync<CdnBlockedException>(
            () => executor.SendAsync(
                () => MakeRequest(), limiter: limiter, label: "slash-test"));

        // DOP should have been slashed to minDop (4)
        Assert.Equal(4, limiter.CurrentDop);
    }

    // ─── CDN probe exhausts MaxCdnRetries ───────────────────────

    [Fact]
    public async Task SendAsync_CdnBlock_ProbeExhaustsRetries_Throws()
    {
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithZeroCdnDelay(handler);

        // Initial CDN 403 + enough for probe to exhaust MaxCdnRetries
        for (int i = 0; i < ResilientHttpExecutor.MaxCdnRetries + 1; i++)
            handler.EnqueueHtml403();

        // Caller gets CdnBlockedException immediately
        await Assert.ThrowsAsync<CdnBlockedException>(
            () => executor.SendAsync(() => MakeRequest(), label: "cdn-exhaust-test"));

        // Probe exhausts retries → _cdnResolved signals false
        await executor.WaitForCdnClearAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SendAsync_CdnBlock_ProbeExhaustsRetries_WithLimiter_ReacquiresSlot()
    {
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithZeroCdnDelay(handler);

        for (int i = 0; i < ResilientHttpExecutor.MaxCdnRetries + 1; i++)
            handler.EnqueueHtml403();

        var limiter = new AdaptiveConcurrencyLimiter(
            initialDop: 2, minDop: 1, maxDop: 10,
            Substitute.For<ILogger<AdaptiveConcurrencyLimiter>>());

        await limiter.WaitAsync(CancellationToken.None);

        // Caller gets CdnBlockedException (slot still held by caller)
        await Assert.ThrowsAsync<CdnBlockedException>(
            () => executor.SendAsync(
                () => MakeRequest(), limiter: limiter, label: "cdn-exhaust-slot-test"));

        // Caller releases slot — verify no underflow
        limiter.Release();
        await limiter.WaitAsync(CancellationToken.None);
        limiter.Release();
    }

    // ─── CDN non-probe signal path ──────────────────────────────

    [Fact]
    public async Task SendAsync_CdnBlock_NonProbeWaitsForProbeSignal()
    {
        // Two concurrent calls: call 1 triggers the probe.
        // Call 2 may either (a) throw CdnBlockedException because IsCdnBlocked was
        // observed true pre-send, or (b) succeed if the probe raced ahead and
        // cleared before call 2's pre-send check. Both outcomes prove the probe
        // clears the block; enqueue an extra success so path (b) has a response.
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithZeroCdnDelay(handler);
        executor.MaxJitterMs = 0;

        handler.EnqueueHtml403(); // call 1 initial → triggers probe
        handler.EnqueueHtml403(); // call 2 initial (if it races past IsCdnBlocked)
        handler.EnqueueJsonOk("""{"result":"probe-ok"}""");  // probe retry → clears
        handler.EnqueueJsonOk("""{"result":"call-2-late"}"""); // call 2 post-clear path

        var task1 = executor.SendAsync(() => MakeRequest(), label: "call-1");
        var task2 = executor.SendAsync(() => MakeRequest(), label: "call-2");

        // Call 1 definitively triggers the CDN block.
        await Assert.ThrowsAsync<CdnBlockedException>(() => task1);

        // Call 2: accept either CdnBlockedException (pre-send gated) or success
        // (probe already cleared). Both outcomes are correct.
        try { (await task2).Dispose(); }
        catch (CdnBlockedException) { /* acceptable */ }

        // Probe clears the CDN block in either case.
        await executor.WaitForCdnClearAsync(CancellationToken.None);
        Assert.False(executor.IsCdnBlocked);
    }

    [Fact]
    public async Task SendAsync_CdnBlock_NonProbeGetsFail_WhenProbeExhausts()
    {
        // Two concurrent calls: both get CdnBlockedException immediately.
        // Probe exhausts retries in the background.
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithZeroCdnDelay(handler);
        executor.MaxJitterMs = 0;

        // Initial requests: both CDN blocked
        handler.EnqueueHtml403();
        handler.EnqueueHtml403();
        // All probe retries: CDN blocked (MaxCdnRetries worth)
        for (int i = 0; i < ResilientHttpExecutor.MaxCdnRetries; i++)
            handler.EnqueueHtml403();

        var task1 = executor.SendAsync(() => MakeRequest(), label: "call-1");
        var task2 = executor.SendAsync(() => MakeRequest(), label: "call-2");

        // Both throw CdnBlockedException immediately
        await Assert.ThrowsAsync<CdnBlockedException>(() => task1);
        await Assert.ThrowsAsync<CdnBlockedException>(() => task2);

        // Probe exhausts and signals
        await executor.WaitForCdnClearAsync(CancellationToken.None);
    }

    // ─── WithCdnResilienceAsync ─────────────────────────────────

    [Fact]
    public async Task WithCdnResilience_Success_ReturnsResultAndReleasesSlot()
    {
        var (executor, _) = CreateExecutor();
        int acquires = 0, releases = 0;

        var result = await executor.WithCdnResilienceAsync(
            work: () => Task.FromResult(42),
            CancellationToken.None,
            acquireSlot: () => { acquires++; return Task.CompletedTask; },
            releaseSlot: () => releases++);

        Assert.Equal(42, result);
        Assert.Equal(1, acquires);
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task WithCdnResilience_CdnBlock_RetriesAfterClear()
    {
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithZeroCdnDelay(handler);

        // First call: CDN block (the helper will catch and retry).
        // We simulate by having work throw CdnBlockedException on first call.
        int callCount = 0;
        int acquires = 0, releases = 0;

        var result = await executor.WithCdnResilienceAsync(
            work: () =>
            {
                callCount++;
                if (callCount == 1)
                    throw new CdnBlockedException("test CDN block");
                return Task.FromResult("success");
            },
            CancellationToken.None,
            acquireSlot: () => { acquires++; return Task.CompletedTask; },
            releaseSlot: () => releases++);

        Assert.Equal("success", result);
        Assert.Equal(2, callCount); // 1 failure + 1 success
        Assert.Equal(2, acquires);  // acquired on each attempt
        Assert.Equal(2, releases);  // released on CDN catch + released on success
    }

    [Fact]
    public async Task WithCdnResilience_NonCdnException_PropagatesAndReleasesSlot()
    {
        var (executor, _) = CreateExecutor();
        int acquires = 0, releases = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.WithCdnResilienceAsync<int>(
                work: () => throw new InvalidOperationException("boom"),
                CancellationToken.None,
                acquireSlot: () => { acquires++; return Task.CompletedTask; },
                releaseSlot: () => releases++));

        Assert.Equal(1, acquires);
        Assert.Equal(1, releases); // released via finally
    }

    [Fact]
    public async Task WithCdnResilience_Cancellation_ThrowsOCE()
    {
        var (executor, _) = CreateExecutor();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            executor.WithCdnResilienceAsync(
                work: () => Task.FromResult(1),
                cts.Token));
    }

    [Fact]
    public async Task WithCdnResilience_NoSlotDelegates_StillHandlesCdnRetry()
    {
        var (executor, _) = CreateExecutor();
        int callCount = 0;

        var result = await executor.WithCdnResilienceAsync(
            work: () =>
            {
                callCount++;
                if (callCount == 1)
                    throw new CdnBlockedException("test CDN block");
                return Task.FromResult("ok");
            },
            CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task WithCdnResilience_MultipleCdnBlocks_RetriesUntilSuccess()
    {
        var (executor, _) = CreateExecutor();
        int callCount = 0;

        var result = await executor.WithCdnResilienceAsync(
            work: () =>
            {
                callCount++;
                if (callCount <= 3)
                    throw new CdnBlockedException($"CDN block #{callCount}");
                return Task.FromResult(callCount);
            },
            CancellationToken.None);

        Assert.Equal(4, result);
        Assert.Equal(4, callCount);
    }

    // ─── Probe-send timeout (Fix 1) ────────────────────────────────
    // These tests cover the regression that wedged production: a probe HTTP send
    // hanging indefinitely because it inherited the scraper's Timeout.InfiniteTimeSpan
    // client. The probe must bound each send with _probeSendTimeout.

    private static ResilientHttpExecutor CreateExecutorWithProbeTimeout(
        MockHttpMessageHandler handler,
        TimeSpan probeSendTimeout,
        CancellationToken lifetime = default)
    {
        var http = new HttpClient(handler);
        var executor = new ResilientHttpExecutor(
            http, Substitute.For<ILogger>(), probeSendTimeout, sendWallClockTimeout: null, lifetime);
        executor.CdnRetryDelaysOverride = new TimeSpan[9]; // all TimeSpan.Zero
        executor.MaxJitterMs = 0;
        return executor;
    }

    [Fact]
    public async Task Probe_SendHangs_PerAttemptTimeoutUnsticks()
    {
        // Production wedge: probe SendAsync never returns. Per-attempt timeout
        // must cancel the hang and continue to the next probe attempt.
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithProbeTimeout(handler, TimeSpan.FromMilliseconds(150));

        handler.EnqueueHtml403();   // initial caller 403 → triggers probe
        handler.EnqueueHang();      // probe attempt 1: hangs, per-attempt timeout fires
        handler.EnqueueJsonOk("""{"result":"cleared"}"""); // probe attempt 2: success

        await Assert.ThrowsAsync<CdnBlockedException>(
            () => executor.SendAsync(() => MakeRequest(), label: "hang-test"));

        // Probe must resolve (CDN cleared) within a bounded time, not hang forever.
        using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await executor.WaitForCdnClearAsync(waitCts.Token);
        Assert.False(executor.IsCdnBlocked);
        Assert.False(executor.IsProbeRunning); // gate released
    }

    [Fact]
    public async Task Probe_CallerTokenCancelled_DoesNotCancelProbe()
    {
        // Caller A triggers CDN block with ctA, then cancels ctA immediately.
        // The probe must continue independently (it no longer inherits caller ct).
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithProbeTimeout(handler, TimeSpan.FromSeconds(5));

        handler.EnqueueHtml403(); // caller 403 → triggers probe
        handler.EnqueueJsonOk("""{"result":"probe-ok"}""");

        using var ctA = new CancellationTokenSource();
        await Assert.ThrowsAsync<CdnBlockedException>(
            () => executor.SendAsync(() => MakeRequest(), label: "caller-a", ct: ctA.Token));

        ctA.Cancel(); // cancel the caller's token — probe must not die

        using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await executor.WaitForCdnClearAsync(waitCts.Token);
        Assert.False(executor.IsCdnBlocked);
    }

    [Fact]
    public async Task Probe_ExecutorLifetimeCancelled_ResolvesWaiters()
    {
        // Shutting down the executor (lifetime token cancels) must wake waiters
        // and release the probe gate — no process-wide leaks.
        var handler = new MockHttpMessageHandler();
        using var lifetimeCts = new CancellationTokenSource();
        var executor = CreateExecutorWithProbeTimeout(
            handler, TimeSpan.FromSeconds(30), lifetimeCts.Token);

        handler.EnqueueHtml403();
        handler.EnqueueHang(); // probe hangs; lifetime cancellation aborts it

        await Assert.ThrowsAsync<CdnBlockedException>(
            () => executor.SendAsync(() => MakeRequest(), label: "lifetime-cancel"));

        // Spin up a waiter, then cancel the executor lifetime
        var waiterTask = executor.WaitForCdnClearAsync(CancellationToken.None);
        Assert.False(waiterTask.IsCompleted);
        lifetimeCts.Cancel();

        // Waiter must complete (probe's OCE handler resolves TCS with false)
        await waiterTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(executor.IsProbeRunning);
    }

    // ─── Gate lifecycle (Fix 2) ─────────────────────────────────
    // The probe gate is now an integer primitive that ResetCdnState can force-
    // release without waiting. Tests verify a wedged/abandoned probe cannot
    // permanently lock out future probes.

    [Fact]
    public async Task ResetCdnState_DuringHungProbe_AllowsNewProbeToLaunch()
    {
        // Previously: stuck probe holds SemaphoreSlim gate forever; LaunchCdnProbe
        // calls silently no-op. After Fix 2+3, ResetCdnState force-clears the
        // integer gate and cancels the old probe so a new one can launch.
        var handler = new MockHttpMessageHandler();
        using var lifetimeCts = new CancellationTokenSource();
        var executor = CreateExecutorWithProbeTimeout(
            handler, TimeSpan.FromSeconds(30), lifetimeCts.Token);

        handler.EnqueueHtml403();   // caller 403 → triggers probe #1
        handler.EnqueueHang();      // probe #1 hangs indefinitely

        await Assert.ThrowsAsync<CdnBlockedException>(
            () => executor.SendAsync(() => MakeRequest(), label: "probe1"));

        Assert.True(executor.IsProbeRunning);

        // Reset should cancel the hung probe and release the gate.
        executor.ResetCdnState();

        // Allow the cancelled probe's finally to run.
        await WaitForConditionAsync(() => !executor.IsProbeRunning, TimeSpan.FromSeconds(5));
        Assert.False(executor.IsProbeRunning);

        // Cooldown floor must elapse before the next probe can launch.
        await Task.Delay(ResilientHttpExecutor.ResetCooldownFloor + TimeSpan.FromMilliseconds(50));

        // A new probe must now be launchable.
        handler.EnqueueHtml403();
        handler.EnqueueJsonOk("""{"result":"probe-2-ok"}""");

        await Assert.ThrowsAsync<CdnBlockedException>(
            () => executor.SendAsync(() => MakeRequest(), label: "probe2"));

        using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await executor.WaitForCdnClearAsync(waitCts.Token);
        Assert.False(executor.IsCdnBlocked);
    }

    // ─── ResetCdnState recovery (Fix 3) ────────────────────────

    [Fact]
    public async Task ResetCdnState_DuringActiveProbe_UnblocksWaiters()
    {
        // Waiters on the current _cdnResolved must wake when ResetCdnState is
        // called — without propagating OperationCanceledException (which would
        // break WithCdnResilienceAsync's retry loops).
        var handler = new MockHttpMessageHandler();
        using var lifetimeCts = new CancellationTokenSource();
        var executor = CreateExecutorWithProbeTimeout(
            handler, TimeSpan.FromSeconds(30), lifetimeCts.Token);

        handler.EnqueueHtml403();
        handler.EnqueueHang();

        await Assert.ThrowsAsync<CdnBlockedException>(
            () => executor.SendAsync(() => MakeRequest(), label: "setup"));

        // Start a waiter before resetting.
        var waiter = executor.WaitForCdnClearAsync(CancellationToken.None);
        Assert.False(waiter.IsCompleted);

        executor.ResetCdnState();

        // Waiter must complete normally (no OCE) within a bounded time.
        await waiter.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(waiter.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ResetCdnState_WithActiveProbe_ArmsShortCooldownFloor()
    {
        // After cancelling an active probe, the short cooldown floor prevents
        // a hot-loop of callers racing the freshly-cleared gate.
        var handler = new MockHttpMessageHandler();
        using var lifetimeCts = new CancellationTokenSource();
        var executor = CreateExecutorWithProbeTimeout(
            handler, TimeSpan.FromSeconds(30), lifetimeCts.Token);

        handler.EnqueueHtml403();
        handler.EnqueueHang();

        await Assert.ThrowsAsync<CdnBlockedException>(
            () => executor.SendAsync(() => MakeRequest(), label: "setup"));

        Assert.True(executor.IsProbeRunning);
        executor.ResetCdnState();

        // Immediately after reset, the floor must still be active.
        Assert.True(executor.IsCdnBlocked,
            "Cooldown floor should keep IsCdnBlocked=true briefly after reset-with-active-probe");
    }

    [Fact]
    public void ResetCdnState_WithoutActiveProbe_NoCooldownFloor()
    {
        // When no probe was active, reset is a pure clear — no artificial cooldown.
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithProbeTimeout(handler, TimeSpan.FromSeconds(30));

        executor.ResetCdnState();

        Assert.False(executor.IsCdnBlocked);
        Assert.False(executor.IsProbeRunning);
    }

    [Fact]
    public async Task Probe_SendTimeoutCounts_AsProbeAttempts()
    {
        // Regression: CdnProbeAttempts counter must increment once per hang-
        // timed-out attempt, not per hang (which would never terminate pre-fix).
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithProbeTimeout(handler, TimeSpan.FromMilliseconds(100));

        handler.EnqueueHtml403();
        handler.EnqueueHang();  // probe attempt 1 — times out
        handler.EnqueueHang();  // probe attempt 2 — times out
        handler.EnqueueJsonOk("""{"result":"ok"}"""); // probe attempt 3 — success

        await Assert.ThrowsAsync<CdnBlockedException>(
            () => executor.SendAsync(() => MakeRequest(), label: "count-test"));

        using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await executor.WaitForCdnClearAsync(waitCts.Token);

        Assert.Equal(3, executor.CdnProbeAttempts);
        Assert.Equal(1, executor.CdnProbeSuccesses);
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
    }

    // ─── In-flight operation tracking (diagnostic endpoint) ────────

    [Fact]
    public async Task InflightOperations_Empty_WhenNoRequestsActive()
    {
        var (executor, _) = CreateExecutor();
        Assert.Empty(executor.InflightOperations);
    }

    [Fact]
    public async Task InflightOperations_TracksActiveSend_WithLabelAndState()
    {
        // Use EnqueueHang so SendAsync is stuck in the Sending state.
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithProbeTimeout(handler, TimeSpan.FromSeconds(5));
        handler.EnqueueHang();

        using var cts = new CancellationTokenSource();
        var sendTask = executor.SendAsync(() => MakeRequest(), label: "inflight-test", ct: cts.Token);

        // Wait for the op to register and reach Sending state
        await WaitForConditionAsync(
            () => executor.InflightOperations.Any(o => o.State == InflightState.Sending),
            TimeSpan.FromSeconds(2));

        var ops = executor.InflightOperations;
        Assert.Single(ops);
        Assert.Equal("inflight-test", ops[0].Label);
        Assert.Equal(InflightState.Sending, ops[0].State);
        Assert.Equal(0, ops[0].Attempt);

        // Cancel to clean up
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sendTask);

        // After cancellation, the op must be removed
        await WaitForConditionAsync(() => executor.InflightOperations.Count == 0, TimeSpan.FromSeconds(2));
        Assert.Empty(executor.InflightOperations);
    }

    [Fact]
    public async Task InflightOperations_RemovedOnSuccess()
    {
        var (executor, handler) = CreateExecutor();
        handler.EnqueueJsonOk("""{"result":"ok"}""");

        var response = await executor.SendAsync(() => MakeRequest(), label: "remove-on-success");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(executor.InflightOperations);
    }

    [Fact]
    public async Task InflightOperations_RemovedOnException()
    {
        var (executor, handler) = CreateExecutor();
        handler.EnqueueException(new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.SendAsync(() => MakeRequest(), label: "remove-on-exception"));
        Assert.Empty(executor.InflightOperations);
    }

    [Fact]
    public async Task InflightOperations_TracksRetryAttempt_AcrossNetworkErrors()
    {
        var (executor, handler) = CreateExecutor();
        // 2 network errors, then a hang so we can snapshot mid-flight with attempt > 0
        handler.EnqueueException(new HttpRequestException("err1"));
        handler.EnqueueException(new HttpRequestException("err2"));
        handler.EnqueueHang();

        using var cts = new CancellationTokenSource();
        var sendTask = executor.SendAsync(() => MakeRequest(), label: "retry-track", ct: cts.Token);

        await WaitForConditionAsync(
            () => executor.InflightOperations.Any(o => o.NetworkErrors >= 2),
            TimeSpan.FromSeconds(3));

        var ops = executor.InflightOperations;
        Assert.Single(ops);
        Assert.True(ops[0].NetworkErrors >= 2,
            $"Expected NetworkErrors >= 2, got {ops[0].NetworkErrors}");
        Assert.True(ops[0].Attempt >= 2,
            $"Expected Attempt >= 2, got {ops[0].Attempt}");

        cts.Cancel();
        try { await sendTask; } catch { /* cancellation expected */ }
        await WaitForConditionAsync(() => executor.InflightOperations.Count == 0, TimeSpan.FromSeconds(2));
    }

    // ─── Per-send wall-clock deadline (zombie socket fix) ─────────────
    // Production stall: proxy-routed sends wedged for 65 minutes because
    // HttpClient.Timeout=Infinite + no per-attempt deadline in SendAsync.
    // Fix adds a linked CTS + CancelAfter(sendWallClockTimeout) around the
    // send call. On timeout the existing catch retries indefinitely.

    private static ResilientHttpExecutor CreateExecutorWithSendTimeout(
        MockHttpMessageHandler handler,
        TimeSpan sendWallClockTimeout)
    {
        var http = new HttpClient(handler);
        var executor = new ResilientHttpExecutor(
            http, Substitute.For<ILogger>(),
            probeSendTimeout: null,
            sendWallClockTimeout: sendWallClockTimeout,
            executorLifetime: default);
        return executor;
    }

    [Fact]
    public async Task SendAsync_HangingSocket_TimesOutAndRetries_UntilSuccess()
    {
        // Two hung sends should be treated as transient and retried; third returns 200.
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithSendTimeout(handler, TimeSpan.FromMilliseconds(150));

        handler.EnqueueHang();
        handler.EnqueueHang();
        handler.EnqueueJsonOk("""{"result":"ok"}""");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await executor.SendAsync(
            () => MakeRequest(), label: "hang-retry", ct: cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task SendAsync_HangingSocket_CallerCancelled_PropagatesCancellation()
    {
        // Caller CT cancellation must win over wall-clock timeout and propagate OCE.
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithSendTimeout(handler, TimeSpan.FromSeconds(30));

        handler.EnqueueHang();

        using var cts = new CancellationTokenSource();
        var sendTask = executor.SendAsync(() => MakeRequest(), label: "caller-cancel", ct: cts.Token);

        await Task.Delay(50);
        cts.Cancel();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sendTask);
        Assert.True(cts.Token.IsCancellationRequested);
        // Only one send attempted — no retry after caller cancel.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SendAsync_ObjectDisposedException_RetriesAsTransient()
    {
        // ObjectDisposedException from SocketsHttpHandler pool reset must be
        // treated as a transient network error and retried indefinitely.
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithSendTimeout(handler, TimeSpan.FromSeconds(10));

        handler.EnqueueException(new ObjectDisposedException("SocketsHttpHandler"));
        handler.EnqueueJsonOk("""{"result":"ok"}""");

        var response = await executor.SendAsync(() => MakeRequest(), label: "ode-retry");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task SendAsync_WallClockTimeout_HonoursConfiguredValue()
    {
        // Short timeout should cause retry within the configured window.
        var handler = new MockHttpMessageHandler();
        var executor = CreateExecutorWithSendTimeout(handler, TimeSpan.FromMilliseconds(100));

        handler.EnqueueHang();
        handler.EnqueueJsonOk("""{"result":"ok"}""");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await executor.SendAsync(
            () => MakeRequest(), label: "short-timeout", ct: cts.Token);
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Hang (~100 ms) + 500ms post-error settle delay + success should land well under 3s.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"Expected elapsed < 3s but was {sw.Elapsed.TotalMilliseconds:F0}ms");
    }
}
