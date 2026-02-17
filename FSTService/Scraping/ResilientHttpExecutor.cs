using System.Net.Http.Headers;

namespace FSTService.Scraping;

/// <summary>
/// Sends HTTP requests with automatic retry on transient failures
/// (429 rate-limit, 5xx server errors, network errors, timeouts).
///
/// <para>Retry behaviour:</para>
/// <list type="bullet">
///   <item>Exponential backoff: 500 ms × 2^(attempt−1)</item>
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
    /// error (e.g. 400, 403). The caller is responsible for reading/disposing
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
}
