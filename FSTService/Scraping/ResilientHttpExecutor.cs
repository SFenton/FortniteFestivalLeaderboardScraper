using System.Net.Http.Headers;

namespace FSTService.Scraping;

/// <summary>
/// Sends HTTP requests with automatic retry on transient failures
/// (429 rate-limit, 5xx server errors, network errors, timeouts)
/// and CDN-level blocks (403 with non-JSON body).
///
/// <para>Retry behaviour:</para>
/// <list type="bullet">
///   <item>Exponential backoff: 500 ms × 2^(attempt−1) for normal retries</item>
///   <item>CDN 403 blocks (non-JSON body) trigger a shared cooldown: all requests
///         on this executor wait until the cooldown expires, then one probe request
///         tests whether the CDN is available again. Schedule: 500 ms, 1 s, 2 s,
///         5 s, 10 s, 15 s, 30 s, 45 s, 60 s — then 60 s indefinitely until
///         the CDN clears or the <see cref="CancellationToken"/> is cancelled</item>
///   <item>429 responses honour the <c>Retry-After</c> header when present</item>
///   <item>Network errors (<see cref="HttpRequestException"/>) and non-cancellation
///         <see cref="TaskCanceledException"/> (timeouts) trigger retry</item>
///   <item>Success/failure is reported to an optional
///         <see cref="AdaptiveConcurrencyLimiter"/> for AIMD DOP adjustment</item>
/// </list>
///
/// Callers are responsible for acquiring/releasing concurrency slots
/// (via <see cref="AdaptiveConcurrencyLimiter.WaitAsync"/>/<see cref="AdaptiveConcurrencyLimiter.Release"/>).
/// This class only <em>reports</em> outcomes so the limiter can adjust its DOP.
///
/// <para><b>CDN block slot management:</b> When a CDN block is detected, the caller's
/// concurrency slot is <em>released</em> for the duration of the cooldown wait so that
/// sleeping tasks do not starve the pool. A slot is reacquired briefly around each
/// probe HTTP send. On all exit paths (success, non-CDN response, cancellation) the
/// method guarantees exactly one slot is held, preserving the caller's
/// acquire/release invariant.</para>
/// </summary>
public sealed class ResilientHttpExecutor
{
    /// <summary>Default maximum retry attempts after the initial try.</summary>
    public const int DefaultMaxRetries = 3;

    /// <summary>Maximum CDN probe retries before giving up. Covers the full 9-step
    /// delay schedule + 6 more at 60 s ≈ 7 minutes total.</summary>
    public const int MaxCdnRetries = 15;

    /// <summary>Base delay for exponential backoff (doubled on each retry).</summary>
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Fixed backoff schedule for CDN-level 403 blocks (non-JSON responses).
    /// These are separate from the normal retry budget.
    /// </summary>
    private static readonly TimeSpan[] DefaultCdnRetryDelays =
    [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(45),
        TimeSpan.FromSeconds(60),
    ];

    /// <summary>Maximum backoff cap for transient-error retries (network errors, timeouts).</summary>
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    /// <summary>Override CDN retry delays for testing (set to zero-delay arrays).</summary>
    internal TimeSpan[]? CdnRetryDelaysOverride { get; set; }

    /// <summary>Maximum jitter (ms) added before non-probe CDN retry attempts.
    /// Set to 0 in tests for determinism.</summary>
    internal int MaxJitterMs { get; set; } = 500;

    private readonly HttpClient _http;
    private readonly ILogger _log;

    // ── CDN wire diagnostics ──────────────────────────────────
    private long _cdnBlocksDetected;
    private long _cdnProbeAttempts;
    private long _cdnProbeSuccesses;
    private long _totalHttpSends;

    /// <summary>Number of times a CDN block (403 non-JSON) was detected.</summary>
    public long CdnBlocksDetected => Volatile.Read(ref _cdnBlocksDetected);
    /// <summary>Number of probe HTTP sends during CDN retry sequences.</summary>
    public long CdnProbeAttempts => Volatile.Read(ref _cdnProbeAttempts);
    /// <summary>Number of probe attempts that returned a non-CDN response (CDN cleared).</summary>
    public long CdnProbeSuccesses => Volatile.Read(ref _cdnProbeSuccesses);
    /// <summary>Total HTTP sends (including probes, retries, everything).</summary>
    public long TotalHttpSends => Volatile.Read(ref _totalHttpSends);

    // ── Shared CDN cooldown state ─────────────────────────────
    // When a CDN block is detected, the probe walks a backoff schedule.
    // Non-probes wait on _cdnResolved (a TCS) for the probe to signal success/failure.
    // The _cdnGate semaphore ensures only one request probes the CDN at a time.
    private DateTimeOffset _cdnCooldownUntil;
    private readonly SemaphoreSlim _cdnGate = new(1, 1);
    private int _cdnRetryIndex; // current position in the delay schedule
    private volatile TaskCompletionSource<bool>? _cdnResolved; // true=CDN clear, false=gave up

    public ResilientHttpExecutor(HttpClient http, ILogger log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// Send an HTTP request with automatic retry on transient failures.
    /// </summary>
    /// <param name="requestFactory">
    /// Factory that creates a <em>new</em> <see cref="HttpRequestMessage"/>
    /// on each invocation (messages cannot be reused after sending).
    /// </param>
    /// <param name="limiter">
    /// Optional adaptive concurrency limiter. Success/failure is reported
    /// for AIMD adjustment; the caller manages slot acquisition/release.
    /// </param>
    /// <param name="label">
    /// Human-readable label used in log messages (e.g. "song/instrument lookup").
    /// </param>
    /// <param name="maxRetries">
    /// Maximum number of retry attempts after the initial try.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The <see cref="HttpResponseMessage"/> on success or on a non-retryable
    /// error (e.g. 400, JSON 403). The caller is responsible for reading/disposing
    /// the response and handling non-retryable errors (such as <c>no_score_found</c>).
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// Thrown when <paramref name="ct"/> is cancelled.
    /// </exception>
    /// <remarks>
    /// Transient network errors (<see cref="HttpRequestException"/>) and non-cancellation
    /// timeouts (<see cref="TaskCanceledException"/>) are retried indefinitely with
    /// capped exponential backoff + limiter feedback. Only <paramref name="ct"/>
    /// cancellation exits. The <paramref name="maxRetries"/> parameter bounds only
    /// HTTP status-code retries (429, 5xx).
    /// </remarks>
    public async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        AdaptiveConcurrencyLimiter? limiter = null,
        string? label = null,
        int maxRetries = DefaultMaxRetries,
        CancellationToken ct = default)
    {
        int statusAttempt = 0; // counts only HTTP status-code retries (429, 5xx)

        for (int attempt = 0; ; attempt++)
        {
            if (attempt > 0)
            {
                // Exponential backoff capped at MaxBackoff, with ±30% jitter
                var baseMs = BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
                if (baseMs > MaxBackoff.TotalMilliseconds) baseMs = MaxBackoff.TotalMilliseconds;
                var jitter = baseMs * (0.7 + Random.Shared.NextDouble() * 0.6); // [0.7, 1.3]
                await Task.Delay(TimeSpan.FromMilliseconds(jitter), ct);
            }

            // ── Wait for any active CDN cooldown before sending ──
            // Release the caller's limiter slot while sleeping so other requests
            // aren't starved. Re-acquire before the actual HTTP send.
            bool rateTokenConsumed = false;
            if (_cdnCooldownUntil > DateTimeOffset.UtcNow && limiter is not null)
            {
                limiter.Release();
                await WaitForCdnCooldownAsync(ct);
                await limiter.WaitAsync(ct);
                rateTokenConsumed = true; // WaitAsync consumed both DOP slot + rate token
            }
            else
            {
                await WaitForCdnCooldownAsync(ct);
            }

            // ── If a CDN probe is active, wait for it instead of wasting an
            //    HTTP send that will certainly get 403'd. Queued tasks that
            //    acquired DOP slots after SlashDop would otherwise drip through
            //    at DOP=4, each making one useless send before entering the
            //    non-probe wait path in RetryCdnBlockAsync. ──
            var activeCdnSignal = _cdnResolved;
            if (activeCdnSignal is not null && !activeCdnSignal.Task.IsCompleted)
            {
                if (limiter is not null)
                {
                    limiter.Release();
                    try
                    {
                        using var reg = ct.Register(() => activeCdnSignal.TrySetCanceled(ct));
                        bool cleared = await activeCdnSignal.Task;
                        if (!cleared)
                            throw new HttpRequestException(
                                $"CDN block on {label ?? "request"} — probe exhausted {MaxCdnRetries} retries.");
                    }
                    finally
                    {
                        await limiter.WaitAsync(ct);
                    }
                    continue; // CDN cleared — retry with full detection
                }
                else
                {
                    using var reg = ct.Register(() => activeCdnSignal.TrySetCanceled(ct));
                    bool cleared = await activeCdnSignal.Task;
                    if (!cleared)
                        throw new HttpRequestException(
                            $"CDN block on {label ?? "request"} — probe exhausted {MaxCdnRetries} retries.");
                    continue;
                }
            }

            // ── Consume a rate token for retries ──
            // The caller's initial WaitAsync consumed a rate token for attempt 0.
            // Retries need their own token unless the CDN cooldown path above
            // already consumed one via WaitAsync.
            if (attempt > 0 && !rateTokenConsumed)
                await (limiter?.AcquireRateTokenAsync(ct) ?? Task.CompletedTask);

            HttpResponseMessage res;
            try
            {
                Interlocked.Increment(ref _totalHttpSends);
                res = await _http.SendAsync(requestFactory(), ct);
            }
            catch (HttpRequestException ex)
            {
                _log.LogWarning(
                    "HTTP error for {Operation} (attempt {Attempt}, DOP {Dop}): {Error}",
                    label ?? "request", attempt + 1, limiter?.CurrentDop ?? -1, ex.Message);
                limiter?.ReportFailure();
                continue; // transient — retry indefinitely
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning(
                    "Timeout for {Operation} (attempt {Attempt}, DOP {Dop})",
                    label ?? "request", attempt + 1, limiter?.CurrentDop ?? -1);
                limiter?.ReportFailure();
                continue; // transient timeout — retry indefinitely
            }

            if (res.IsSuccessStatusCode)
            {
                limiter?.ReportSuccess();
                return res;
            }

            var statusCode = (int)res.StatusCode;

            // ── CDN block detection (403 with non-JSON body) ──────────
            if (statusCode == 403)
            {
                var body = await res.Content.ReadAsStringAsync(ct);
                bool isCdnBlock = !body.TrimStart().StartsWith('{');

                if (isCdnBlock)
                {
                    Interlocked.Increment(ref _cdnBlocksDetected);
                    res.Dispose();
                    var cdnResult = await RetryCdnBlockAsync(requestFactory, limiter, label, ct);
                    if (cdnResult is null)
                        continue; // Non-probe: CDN cleared, retry with full detection
                    return cdnResult; // Probe: return actual response
                }

                // JSON 403 — re-wrap the consumed body so caller can still read it
                var mediaType = res.Content.Headers.ContentType?.MediaType ?? "application/json";
                res.Content.Dispose();
                res.Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType);
            }

            bool retryable = statusCode == 429 || statusCode >= 500;

            if (retryable && statusAttempt < maxRetries)
            {
                statusAttempt++;

                // Honour Retry-After header on 429
                if (statusCode == 429 && res.Headers.RetryAfter?.Delta is TimeSpan retryAfter)
                {
                    _log.LogWarning(
                        "Rate-limited on {Operation}, waiting {Delay:F1}s (DOP {Dop})",
                        label ?? "request", retryAfter.TotalSeconds, limiter?.CurrentDop ?? -1);
                    limiter?.ReportFailure();
                    res.Dispose();
                    await Task.Delay(retryAfter, ct);
                    continue;
                }

                _log.LogWarning(
                    "{StatusCode} for {Operation} (attempt {Attempt}/{MaxAttempts}, DOP {Dop})",
                    statusCode, label ?? "request", statusAttempt, maxRetries + 1, limiter?.CurrentDop ?? -1);
                limiter?.ReportFailure();
                res.Dispose();
                continue;
            }

            // Non-retryable status or status-code retries exhausted — let caller decide.
            // Report failure only for retryable codes that exhausted retries;
            // non-retryable codes (400, 403, 404, …) are not "failures" for
            // the adaptive limiter (the server handled the request properly).
            if (retryable)
                limiter?.ReportFailure();

            return res;
        }
    }

    /// <summary>
    /// Handle a CDN-blocked request. The first request to detect a block becomes
    /// the "probe" — it slashes DOP, walks the backoff schedule, and signals all
    /// waiters when the CDN clears (or gives up after <see cref="MaxCdnRetries"/>).
    /// Non-probes simply wait for the probe's signal, then return <c>null</c> to
    /// <see cref="SendAsync"/> which loops back to re-execute with full CDN detection.
    /// This eliminates thundering-herd stampedes.
    ///
    /// <para><b>Slot lifecycle:</b> The caller's DOP slot is released at entry so that
    /// sleeping tasks do not starve the pool. A slot is reacquired on all exit paths
    /// (success, probe failure, cancellation) preserving the caller's finally-Release
    /// invariant.</para>
    /// </summary>
    private async Task<HttpResponseMessage?> RetryCdnBlockAsync(
        Func<HttpRequestMessage> requestFactory,
        AdaptiveConcurrencyLimiter? limiter,
        string? label,
        CancellationToken ct)
    {
        var delays = CdnRetryDelaysOverride ?? DefaultCdnRetryDelays;

        // Release the caller's slot immediately — we'll be sleeping, not using bandwidth.
        bool slotHeld = false;
        limiter?.Release();

        try
        {
            // Try to become the probe. Only one request probes at a time.
            if (!_cdnGate.Wait(0))
            {
                // ── Non-probe path ──────────────────────────────────────
                // Wait for the probe to resolve. On success, reacquire slot and
                // return to SendAsync to re-execute the original request.
                // On failure or cancellation, reacquire slot and throw.
                _log.LogDebug("CDN block on {Operation} — waiting for probe to clear.", label ?? "request");

                // Spin until the probe has created its TCS. There's a brief race
                // window between the probe winning _cdnGate and setting _cdnResolved.
                // Without this spin, non-probes fall through with null, return to
                // SendAsync, make another HTTP request hitting CDN, and double the
                // block count.
                var resolved = _cdnResolved;
                while (resolved is null)
                {
                    await Task.Yield();
                    resolved = _cdnResolved;
                }

                {
                    using var reg = ct.Register(() => resolved.TrySetCanceled(ct));
                    bool cdnCleared = await resolved.Task; // throws if cancelled

                    if (!cdnCleared)
                    {
                        // Probe gave up after MaxCdnRetries
                        if (limiter is not null) { await limiter.WaitAsync(ct); slotHeld = true; }
                        throw new HttpRequestException(
                            $"CDN block on {label ?? "request"} — probe exhausted {MaxCdnRetries} retries.");
                    }
                }

                // CDN is clear — reacquire slot and return null to SendAsync,
                // which will loop back and re-execute with full CDN detection.
                if (limiter is not null) { await limiter.WaitAsync(ct); slotHeld = true; }
                return null;
            }

            // ── Probe path ──────────────────────────────────────────
            // We are the probe. Slash DOP, create signal, walk retry schedule.
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _cdnResolved = tcs;

            // Immediately slash DOP to minDop — CDN blocks are binary, not gradual.
            limiter?.SlashDop();

            try
            {
                for (int i = _cdnRetryIndex; i < MaxCdnRetries; i++)
                {
                    _cdnRetryIndex = i + 1;

                    var delay = i < delays.Length ? delays[i] : delays[^1];

                    // Set cooldown timestamp (used by WaitForCdnCooldownAsync in SendAsync)
                    _cdnCooldownUntil = DateTimeOffset.UtcNow + delay;

                    _log.LogWarning(
                        "CDN block on {Operation} (CDN retry {CdnAttempt}/{MaxRetries}), waiting {Delay:F1}s",
                        label ?? "request", i + 1, MaxCdnRetries, delay.TotalSeconds);
                    limiter?.ReportFailure();

                    await Task.Delay(delay, ct);

                    // Acquire only a rate token — NOT a DOP slot. The probe must
                    // not compete for DOP slots because after SlashDop(575→4) the
                    // release-debt mechanism absorbs all returns. Non-probes are
                    // blocked on our TCS so they can't pay off the debt, creating
                    // a deadlock if we wait on the semaphore here.
                    await (limiter?.AcquireRateTokenAsync(ct) ?? Task.CompletedTask);

                    HttpResponseMessage res;
                    try
                    {
                        _log.LogInformation(
                            "CDN probe sending HTTP request (attempt {CdnAttempt}/{MaxRetries}, wire sends so far: {WireSends})",
                            i + 1, MaxCdnRetries, TotalHttpSends);
                        Interlocked.Increment(ref _cdnProbeAttempts);
                        Interlocked.Increment(ref _totalHttpSends);
                        res = await _http.SendAsync(requestFactory(), ct);
                        _log.LogInformation(
                            "CDN probe got response: {StatusCode} (attempt {CdnAttempt}/{MaxRetries})",
                            (int)res.StatusCode, i + 1, MaxCdnRetries);
                    }
                    catch (HttpRequestException ex)
                    {
                        _log.LogWarning("CDN probe HTTP error (attempt {CdnAttempt}): {Error}", i + 1, ex.Message);
                        continue; // probe has no DOP slot to release
                    }
                    catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                    {
                        _log.LogWarning("CDN probe timed out (attempt {CdnAttempt})", i + 1);
                        continue; // probe has no DOP slot to release
                    }

                    if (res.IsSuccessStatusCode)
                    {
                        // CDN is back — clear state, signal waiters (unblocking
                        // non-probes whose releases pay off DOP debt), then
                        // reacquire a real slot for the caller.
                        Interlocked.Increment(ref _cdnProbeSuccesses);
                        _cdnCooldownUntil = default;
                        _cdnRetryIndex = 0;
                        tcs.TrySetResult(true);
                        _log.LogWarning(
                            "CDN cleared on {Operation} after {ProbeAttempt} probes (total sends: {TotalSends}, blocks: {Blocks})",
                            label ?? "request", i + 1, TotalHttpSends, CdnBlocksDetected);
                        if (limiter is not null) { await limiter.WaitAsync(ct); slotHeld = true; }
                        limiter?.ReportSuccess();
                        return res;
                    }

                    if ((int)res.StatusCode == 403)
                    {
                        var body = await res.Content.ReadAsStringAsync(ct);
                        if (!body.TrimStart().StartsWith('{'))
                        {
                            res.Dispose();
                            continue; // Still CDN blocked — probe has no DOP slot
                        }

                        // JSON 403 — CDN is clear, this is an API error
                        Interlocked.Increment(ref _cdnProbeSuccesses);
                        _cdnCooldownUntil = default;
                        _cdnRetryIndex = 0;
                        tcs.TrySetResult(true);
                        _log.LogWarning(
                            "CDN cleared on {Operation} (JSON 403) after {ProbeAttempt} probes (total sends: {TotalSends}, blocks: {Blocks})",
                            label ?? "request", i + 1, TotalHttpSends, CdnBlocksDetected);
                        res.Content.Dispose();
                        res.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                        if (limiter is not null) { await limiter.WaitAsync(ct); slotHeld = true; }
                        return res;
                    }

                    // Different status — CDN is clear
                    Interlocked.Increment(ref _cdnProbeSuccesses);
                    _cdnCooldownUntil = default;
                    _cdnRetryIndex = 0;
                    tcs.TrySetResult(true);
                    _log.LogWarning(
                        "CDN cleared on {Operation} ({StatusCode}) after {ProbeAttempt} probes (total sends: {TotalSends}, blocks: {Blocks})",
                        label ?? "request", (int)res.StatusCode, i + 1, TotalHttpSends, CdnBlocksDetected);
                    if (limiter is not null) { await limiter.WaitAsync(ct); slotHeld = true; }
                    return res;
                }

                // Exhausted all retries — signal failure to waiters
                _log.LogError(
                    "CDN block on {Operation} — gave up after {MaxRetries} retries.",
                    label ?? "request", MaxCdnRetries);
                _cdnCooldownUntil = default;
                _cdnRetryIndex = 0;
                tcs.TrySetResult(false);

                if (limiter is not null) { await limiter.WaitAsync(ct); slotHeld = true; }
                throw new HttpRequestException(
                    $"CDN block on {label ?? "request"} — probe exhausted {MaxCdnRetries} retries.");
            }
            finally
            {
                tcs.TrySetCanceled();
                _cdnGate.Release();
            }
        }
        catch
        {
            // On any exception (OperationCanceledException, etc.), we must guarantee
            // the caller's invariant: exactly one slot is held when we return/throw,
            // because the caller's finally block will call limiter.Release().
            //
            // EXCEPT during cancellation: after SlashDop, the semaphore may have 0
            // tokens with massive release-debt. WaitAsync(CancellationToken.None)
            // would block forever. During shutdown the caller's Release() will
            // safely absorb debt instead of returning a phantom token.
            if (!slotHeld && limiter is not null && !ct.IsCancellationRequested)
            {
                try
                {
                    await limiter.WaitAsync(CancellationToken.None);
                }
                catch
                {
                    // Limiter disposed during shutdown — caller's Release() will be a
                    // harmless no-op on a disposed semaphore. Let the original exception propagate.
                }
            }
            throw;
        }
    }

    /// <summary>
    /// Reset CDN cooldown state. Call at the start of each scrape pass to prevent
    /// stale state from a previous pass imposing unnecessarily long cooldowns.
    /// </summary>
    public void ResetCdnState()
    {
        _cdnCooldownUntil = default;
        _cdnRetryIndex = 0;
        _cdnResolved = null;
    }

    /// <summary>
    /// If a CDN cooldown is active, wait until it expires.
    /// </summary>
    private async Task WaitForCdnCooldownAsync(CancellationToken ct)
    {
        var remaining = _cdnCooldownUntil - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, ct);
    }
}
