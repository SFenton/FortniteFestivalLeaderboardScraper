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
/// </summary>
public sealed class ResilientHttpExecutor
{
    /// <summary>Default maximum retry attempts after the initial try.</summary>
    public const int DefaultMaxRetries = 3;

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

    // ── Shared CDN cooldown state ─────────────────────────────
    // When a CDN block is detected, _cdnCooldownUntil is set to a future time.
    // All concurrent SendAsync calls wait until this time before sending.
    // The _cdnGate semaphore ensures only one request probes the CDN at a time.
    private DateTimeOffset _cdnCooldownUntil;
    private readonly SemaphoreSlim _cdnGate = new(1, 1);
    private int _cdnRetryIndex; // current position in the delay schedule

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
                // Exponential backoff capped at MaxBackoff
                var backoff = TimeSpan.FromMilliseconds(
                    BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                if (backoff > MaxBackoff) backoff = MaxBackoff;
                await Task.Delay(backoff, ct);
            }

            // ── Wait for any active CDN cooldown before sending ──
            await WaitForCdnCooldownAsync(ct);

            HttpResponseMessage res;
            try
            {
                res = await _http.SendAsync(requestFactory(), ct);
            }
            catch (HttpRequestException ex)
            {
                _log.LogWarning(
                    "HTTP error for {Operation} (attempt {Attempt}): {Error}",
                    label ?? "request", attempt + 1, ex.Message);
                limiter?.ReportFailure();
                continue; // transient — retry indefinitely
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning(
                    "Timeout for {Operation} (attempt {Attempt})",
                    label ?? "request", attempt + 1);
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
                    res.Dispose();
                    return await RetryCdnBlockAsync(requestFactory, limiter, label, ct);
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
                        "Rate-limited on {Operation}, waiting {Delay:F1}s",
                        label ?? "request", retryAfter.TotalSeconds);
                    limiter?.ReportFailure();
                    res.Dispose();
                    await Task.Delay(retryAfter, ct);
                    continue;
                }

                _log.LogWarning(
                    "{StatusCode} for {Operation} (attempt {Attempt}/{MaxAttempts})",
                    statusCode, label ?? "request", statusAttempt, maxRetries + 1);
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
    /// Handle a CDN-blocked request using a shared cooldown. The first request to
    /// detect a CDN block sets a cooldown timestamp and becomes the "probe" — it
    /// walks the extended backoff schedule, testing the CDN after each delay.
    /// All other concurrent requests simply wait for the cooldown to pass, then
    /// retry their own request normally. This prevents all requests from hammering
    /// the CDN in parallel during a block.
    /// </summary>
    private async Task<HttpResponseMessage> RetryCdnBlockAsync(
        Func<HttpRequestMessage> requestFactory,
        AdaptiveConcurrencyLimiter? limiter,
        string? label,
        CancellationToken ct)
    {
        var delays = CdnRetryDelaysOverride ?? DefaultCdnRetryDelays;

        // Try to become the probe. Only one request probes at a time.
        if (!_cdnGate.Wait(0))
        {
            // Another request is already probing — loop: wait for cooldown, jitter, attempt.
            // Mirrors the probe's infinite retry pattern. Only CancellationToken exits.
            _log.LogDebug("CDN block on {Operation} — waiting for probe to clear cooldown.", label ?? "request");
            while (true)
            {
                await WaitForCdnCooldownAsync(ct);

                // Jitter to stagger non-probe requests and avoid thundering herd
                if (MaxJitterMs > 0)
                    await Task.Delay(Random.Shared.Next(MaxJitterMs), ct);

                limiter?.ReportFailure();

                HttpResponseMessage nonProbeRes;
                try
                {
                    nonProbeRes = await _http.SendAsync(requestFactory(), ct);
                }
                catch (HttpRequestException)
                {
                    continue; // transient error — wait for next cooldown cycle
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    continue; // timeout — wait for next cooldown cycle
                }

                if (nonProbeRes.IsSuccessStatusCode)
                {
                    limiter?.ReportSuccess();
                    return nonProbeRes;
                }

                if ((int)nonProbeRes.StatusCode == 403)
                {
                    var body = await nonProbeRes.Content.ReadAsStringAsync(ct);
                    if (!body.TrimStart().StartsWith('{'))
                    {
                        nonProbeRes.Dispose();
                        continue; // still CDN blocked — loop back to cooldown
                    }

                    // JSON 403 — CDN is clear, this is an API error
                    nonProbeRes.Content.Dispose();
                    nonProbeRes.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                    return nonProbeRes;
                }

                // Non-CDN response — return to caller
                return nonProbeRes;
            }
        }

        // We are the probe. Walk the retry schedule, then continue at 60 s indefinitely.
        // CDN blocks are always temporary — CancellationToken is the only exit.
        try
        {
            for (int i = _cdnRetryIndex; ; i++)
            {
                _cdnRetryIndex = i + 1;

                var delay = i < delays.Length ? delays[i] : delays[^1];

                // Set cooldown so all other requests wait
                _cdnCooldownUntil = DateTimeOffset.UtcNow + delay;

                _log.LogWarning(
                    "CDN block on {Operation} (CDN retry {CdnAttempt}), waiting {Delay:F1}s",
                    label ?? "request", i + 1, delay.TotalSeconds);
                limiter?.ReportFailure();

                await Task.Delay(delay, ct);

                HttpResponseMessage res;
                try
                {
                    res = await _http.SendAsync(requestFactory(), ct);
                }
                catch (HttpRequestException)
                {
                    continue;
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    continue;
                }

                if (res.IsSuccessStatusCode)
                {
                    // CDN is back — clear cooldown and reset retry index
                    _cdnCooldownUntil = default;
                    _cdnRetryIndex = 0;
                    limiter?.ReportSuccess();
                    return res;
                }

                if ((int)res.StatusCode == 403)
                {
                    var body = await res.Content.ReadAsStringAsync(ct);
                    if (!body.TrimStart().StartsWith('{'))
                    {
                        res.Dispose();
                        continue; // Still CDN blocked — try next delay
                    }

                    // JSON 403 — CDN is clear, this is an API error
                    _cdnCooldownUntil = default;
                    _cdnRetryIndex = 0;
                    res.Content.Dispose();
                    res.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                    return res;
                }

                // Different status — CDN is clear
                _cdnCooldownUntil = default;
                _cdnRetryIndex = 0;
                return res;
            }
        }
        finally
        {
            _cdnGate.Release();
        }
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
