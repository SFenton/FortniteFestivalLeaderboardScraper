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
///   <item>CDN 403 blocks (non-JSON body) use an extended fixed schedule:
///         500 ms, 1 s, 2 s, 5 s, 10 s, 15 s, 30 s, 45 s, 60 s</item>
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

    /// <summary>Override CDN retry delays for testing (set to zero-delay arrays).</summary>
    internal TimeSpan[]? CdnRetryDelaysOverride { get; set; }

    private readonly HttpClient _http;
    private readonly ILogger _log;

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
    /// <exception cref="HttpRequestException">
    /// Thrown when a network error persists after all retries.
    /// </exception>
    /// <exception cref="TaskCanceledException">
    /// Thrown when a timeout persists after all retries, or when
    /// <paramref name="ct"/> is cancelled.
    /// </exception>
    public async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        AdaptiveConcurrencyLimiter? limiter = null,
        string? label = null,
        int maxRetries = DefaultMaxRetries,
        CancellationToken ct = default)
    {
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            if (attempt > 0)
            {
                // Exponential backoff: 500 ms, 1 s, 2 s, …
                var backoff = TimeSpan.FromMilliseconds(
                    BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                await Task.Delay(backoff, ct);
            }

            HttpResponseMessage res;
            try
            {
                res = await _http.SendAsync(requestFactory(), ct);
            }
            catch (HttpRequestException ex) when (attempt < maxRetries)
            {
                _log.LogWarning(
                    "HTTP error for {Operation} (attempt {Attempt}/{MaxAttempts}): {Error}",
                    label ?? "request", attempt + 1, maxRetries + 1, ex.Message);
                limiter?.ReportFailure();
                continue;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < maxRetries)
            {
                _log.LogWarning(
                    "Timeout for {Operation} (attempt {Attempt}/{MaxAttempts})",
                    label ?? "request", attempt + 1, maxRetries + 1);
                limiter?.ReportFailure();
                continue;
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

            if (retryable && attempt < maxRetries)
            {
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
                    statusCode, label ?? "request", attempt + 1, maxRetries + 1);
                limiter?.ReportFailure();
                res.Dispose();
                continue;
            }

            // Non-retryable status or retries exhausted — let caller decide.
            // Report failure only for retryable codes that exhausted retries;
            // non-retryable codes (400, 403, 404, …) are not "failures" for
            // the adaptive limiter (the server handled the request properly).
            if (retryable)
                limiter?.ReportFailure();

            return res;
        }

        // Unreachable: the loop always returns or throws.
        throw new InvalidOperationException("Retry loop exited unexpectedly.");
    }

    /// <summary>
    /// Retry a CDN-blocked request (403 with non-JSON body) using the extended
    /// backoff schedule. CDN retries are separate from the normal retry budget.
    /// Each retry reports failure to the limiter so AIMD backs off DOP.
    /// The limiter slot is released during CDN waits so other work can proceed,
    /// then re-acquired before the next attempt.
    /// </summary>
    private async Task<HttpResponseMessage> RetryCdnBlockAsync(
        Func<HttpRequestMessage> requestFactory,
        AdaptiveConcurrencyLimiter? limiter,
        string? label,
        CancellationToken ct)
    {
        var delays = CdnRetryDelaysOverride ?? DefaultCdnRetryDelays;
        HttpResponseMessage? lastRes = null;

        for (int i = 0; i < delays.Length; i++)
        {
            _log.LogWarning(
                "CDN block on {Operation} (CDN retry {CdnAttempt}/{CdnMax}), waiting {Delay:F1}s",
                label ?? "request", i + 1, delays.Length, delays[i].TotalSeconds);
            limiter?.ReportFailure();

            // Release the limiter slot while waiting so other work can proceed
            limiter?.Release();
            try
            {
                await Task.Delay(delays[i], ct);
            }
            finally
            {
                // Re-acquire before retrying (even if cancelled, so slot accounting stays balanced)
                if (limiter is not null)
                    await limiter.WaitAsync(ct);
            }

            HttpResponseMessage res;
            try
            {
                res = await _http.SendAsync(requestFactory(), ct);
            }
            catch (HttpRequestException)
            {
                continue; // Network error during CDN retry — try next delay
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                continue; // Timeout during CDN retry — try next delay
            }

            lastRes?.Dispose();

            if (res.IsSuccessStatusCode)
            {
                limiter?.ReportSuccess();
                return res;
            }

            // Check if still a CDN block
            if ((int)res.StatusCode == 403)
            {
                var body = await res.Content.ReadAsStringAsync(ct);
                if (!body.TrimStart().StartsWith('{'))
                {
                    lastRes = res;
                    continue; // Still CDN blocked — try next delay
                }

                // Got a JSON 403 — re-wrap body and return for normal handling
                res.Content.Dispose();
                res.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                return res;
            }

            // Different status code — return for caller to handle
            return res;
        }

        // Exhausted CDN retries
        _log.LogWarning(
            "CDN block on {Operation} persisted after {CdnMax} retries. Giving up.",
            label ?? "request", delays.Length);
        limiter?.ReportFailure();

        return lastRes ?? new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden);
    }
}
