using System.Net.Http.Headers;

namespace FSTService.Scraping;

/// <summary>
/// Thrown by <see cref="ResilientHttpExecutor"/> when a CDN block is detected.
/// The caller should release its DOP slot, await <see cref="ResilientHttpExecutor.WaitForCdnClearAsync"/>,
/// then re-acquire and retry. A background probe runs independently.
/// </summary>
public sealed class CdnBlockedException : Exception
{
    public CdnBlockedException(string message) : base(message) { }
}

/// <summary>State of a CDN probe attempt, fired via <see cref="ResilientHttpExecutor.OnCdnProbeEvent"/>.</summary>
public enum CdnProbeState
{
    /// <summary>Waiting before the next probe attempt (delay countdown).</summary>
    Waiting,
    /// <summary>Sending a probe HTTP request.</summary>
    Probing,
    /// <summary>Probe succeeded — CDN block is cleared.</summary>
    Cleared,
    /// <summary>All probe retries exhausted — gave up.</summary>
    Exhausted,
}

/// <summary>Event fired during CDN probe lifecycle.</summary>
public readonly record struct CdnProbeEvent(CdnProbeState State, int Attempt, int MaxRetries, double NextRetrySeconds);

/// <summary>Coarse-grained state of an in-flight <see cref="ResilientHttpExecutor.SendAsync"/> call.
/// Used by the <c>/api/diag/inflight</c> endpoint to diagnose stuck scrapes.</summary>
public enum InflightState
{
    /// <summary>Awaiting <see cref="ResilientHttpExecutor.WaitForCdnClearAsync"/> (CDN block active).</summary>
    WaitingForCdnClear,
    /// <summary>Between retries: sleeping for exponential backoff or post-error settle delay.</summary>
    BackoffDelay,
    /// <summary>Awaiting a rate-limiter token (queued).</summary>
    AcquiringRateToken,
    /// <summary>HTTP send in progress (includes TCP connect + TLS + response headers).</summary>
    Sending,
    /// <summary>Reading/parsing the response body (e.g. for CDN-block detection).</summary>
    ReadingBody,
}

/// <summary>Snapshot of a single in-flight operation.</summary>
public sealed record InflightOperationSnapshot(
    Guid OperationId,
    string Label,
    InflightState State,
    int Attempt,
    int StatusAttempts,
    int NetworkErrors,
    DateTimeOffset StartedAt,
    DateTimeOffset StateEnteredAt);

/// <summary>
/// Mutable per-op tracking record, kept in a <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/>.
/// State transitions are marked via <see cref="SetState"/>.
/// </summary>
internal sealed class InflightOperation
{
    public Guid OperationId { get; } = Guid.NewGuid();
    public string Label { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    private long _stateEnteredTicks;
    private int _state;
    private int _attempt;
    private int _statusAttempts;
    private int _networkErrors;

    public InflightOperation(string label)
    {
        Label = label;
        _stateEnteredTicks = StartedAt.UtcTicks;
    }

    public void SetState(InflightState state)
    {
        Interlocked.Exchange(ref _state, (int)state);
        Interlocked.Exchange(ref _stateEnteredTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public void SetAttempt(int attempt, int statusAttempts, int networkErrors)
    {
        Volatile.Write(ref _attempt, attempt);
        Volatile.Write(ref _statusAttempts, statusAttempts);
        Volatile.Write(ref _networkErrors, networkErrors);
    }

    public InflightOperationSnapshot Snapshot() => new(
        OperationId,
        Label,
        (InflightState)Volatile.Read(ref _state),
        Volatile.Read(ref _attempt),
        Volatile.Read(ref _statusAttempts),
        Volatile.Read(ref _networkErrors),
        StartedAt,
        new DateTimeOffset(Volatile.Read(ref _stateEnteredTicks), TimeSpan.Zero));
}

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
    public const int DefaultMaxRetries = 10;

    /// <summary>Maximum CDN probe retries before giving up. Covers the full 9-step
    /// delay schedule + 6 more at 60 s ≈ 7 minutes total.</summary>
    public const int MaxCdnRetries = 30;

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

    // ── In-flight operation tracking (for /api/diag/inflight) ──────────
    // Keyed by Guid so removal is O(1) and independent of label collisions.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, InflightOperation> _inflight = new();

    /// <summary>Snapshot of all in-flight SendAsync operations, oldest first.
    /// Used by the diagnostic endpoint to identify stuck requests.</summary>
    public IReadOnlyList<InflightOperationSnapshot> InflightOperations
    {
        get
        {
            var list = new List<InflightOperationSnapshot>(_inflight.Count);
            foreach (var op in _inflight.Values)
                list.Add(op.Snapshot());
            list.Sort((a, b) => a.StartedAt.CompareTo(b.StartedAt));
            return list;
        }
    }

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

    /// <summary>
    /// Optional callback fired during CDN probe lifecycle. Set by the caller
    /// (e.g. <see cref="CyclicalSongMachine"/>) to propagate probe state to
    /// per-user sync progress trackers.
    /// </summary>
    public Action<CdnProbeEvent>? OnCdnProbeEvent { get; set; }

    // ── Shared CDN cooldown state ─────────────────────────────
    // When a CDN block is detected, the probe walks a backoff schedule.
    // Non-probes wait on _cdnResolved (a TCS) for the probe to signal success/failure.
    // The _probeRunning int (0/1) ensures only one probe runs at a time; it is an
    // integer primitive (not a SemaphoreSlim) so ResetCdnState can forcibly clear it
    // if a probe is abandoned/cancelled without deadlocking future probe launches.
    private DateTimeOffset _cdnCooldownUntil;
    private int _probeRunning; // 0 = no probe, 1 = probe in flight
    private int _cdnRetryIndex; // current position in the delay schedule
    private volatile TaskCompletionSource<bool>? _cdnResolved; // true=CDN clear, false=gave up
    private Task? _probeTask; // background probe task (fire-and-forget with TCS signal)
    private volatile CancellationTokenSource? _probeCts; // scoped to in-flight probe; null when idle

    // ── Probe bounds & lifetime (Fix 1) ───────────────────────
    /// <summary>Default per-attempt timeout for probe HTTP sends. Prevents a single
    /// probe send from hanging indefinitely (e.g. on a wedged proxy connection).</summary>
    public static readonly TimeSpan DefaultProbeSendTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _probeSendTimeout;
    private readonly CancellationToken _executorLifetime;

    public ResilientHttpExecutor(HttpClient http, ILogger log)
        : this(http, log, probeSendTimeout: null, executorLifetime: default) { }

    /// <param name="probeSendTimeout">Per-attempt timeout for the background CDN probe
    /// send. Defaults to <see cref="DefaultProbeSendTimeout"/>. A single probe
    /// <c>HttpClient.SendAsync</c> call will not block longer than this, regardless of
    /// the underlying client's <c>Timeout</c>.</param>
    /// <param name="executorLifetime">Token tied to the executor's owner (typically
    /// <c>IHostApplicationLifetime.ApplicationStopping</c>). The background probe uses
    /// this token for outer delays and as the base of its per-attempt send token, so
    /// the probe is decoupled from any individual caller's cancellation token.</param>
    public ResilientHttpExecutor(
        HttpClient http,
        ILogger log,
        TimeSpan? probeSendTimeout,
        CancellationToken executorLifetime)
    {
        _http = http;
        _log = log;
        _probeSendTimeout = probeSendTimeout ?? DefaultProbeSendTimeout;
        _executorLifetime = executorLifetime;
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
        int networkErrors = 0; // counts transient network errors (not counted toward retries)

        var op = new InflightOperation(label ?? "request");
        _inflight[op.OperationId] = op;
        try
        {
        for (int attempt = 0; ; attempt++)
        {
            op.SetAttempt(attempt, statusAttempt, networkErrors);

            // If CDN is blocked, throw immediately — don't waste a wire send.
            // The caller (SongMachine) will release its DOP slot and wait for the probe.
            if (IsCdnBlocked)
                throw new CdnBlockedException(
                    $"CDN block active on {label ?? "request"} (pre-send check, attempt {attempt + 1})");

            if (statusAttempt > 0)
            {
                // Exponential backoff capped at MaxBackoff, with ±30% jitter
                // Only back off based on status-code retries, not transient network errors
                var baseMs = BaseDelay.TotalMilliseconds * Math.Pow(2, statusAttempt - 1);
                if (baseMs > MaxBackoff.TotalMilliseconds) baseMs = MaxBackoff.TotalMilliseconds;
                var jitter = baseMs * (0.7 + Random.Shared.NextDouble() * 0.6); // [0.7, 1.3]
                op.SetState(InflightState.BackoffDelay);
                await Task.Delay(TimeSpan.FromMilliseconds(jitter), ct);
            }
            else if (networkErrors > 0)
            {
                // Short fixed delay for network errors (proxy reconnecting)
                op.SetState(InflightState.BackoffDelay);
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }

            // ── Consume a rate token for retries ──
            // The caller's initial WaitAsync consumed a rate token for attempt 0.
            if (attempt > 0)
            {
                op.SetState(InflightState.AcquiringRateToken);
                await (limiter?.AcquireRateTokenAsync(ct) ?? Task.CompletedTask);
            }

            HttpResponseMessage res;
            try
            {
                Interlocked.Increment(ref _totalHttpSends);
                op.SetState(InflightState.Sending);
                res = await _http.SendAsync(requestFactory(), ct);
            }
            catch (HttpRequestException ex)
            {
                if (IsCdnBlocked)
                    throw new CdnBlockedException(
                        $"CDN block on {label ?? "request"} (network error during CDN block: {ex.Message})");

                networkErrors++;
                _log.LogWarning(
                    "HTTP error for {Operation} (networkError {NetErr}, DOP {Dop}): {Error}",
                    label ?? "request", networkErrors, limiter?.CurrentDop ?? -1, ex.Message);
                limiter?.ReportFailure();
                continue; // transient — retry indefinitely
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                if (IsCdnBlocked)
                    throw new CdnBlockedException(
                        $"CDN block on {label ?? "request"} (timeout during CDN block)");

                networkErrors++;
                _log.LogWarning(
                    "Timeout for {Operation} (networkError {NetErr}, DOP {Dop})",
                    label ?? "request", networkErrors, limiter?.CurrentDop ?? -1);
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
            // On CDN block: launch a background probe (if not already running)
            // and throw CdnBlockedException immediately. The caller is responsible
            // for releasing its DOP slot, waiting for WaitForCdnClearAsync(), and retrying.
            if (statusCode == 403)
            {
                op.SetState(InflightState.ReadingBody);
                var body = await res.Content.ReadAsStringAsync(ct);
                bool isCdnBlock = !body.TrimStart().StartsWith('{');

                if (isCdnBlock)
                {
                    Interlocked.Increment(ref _cdnBlocksDetected);
                    res.Dispose();
                    limiter?.ReportFailure();
                    limiter?.SlashDop();
                    LaunchCdnProbe(requestFactory, limiter, label, ct);
                    throw new CdnBlockedException(
                        $"CDN block on {label ?? "request"} (wire sends: {TotalHttpSends}, blocks: {CdnBlocksDetected})");
                }

                // JSON 403 — re-wrap the consumed body so caller can still read it
                var mediaType = res.Content.Headers.ContentType?.MediaType ?? "application/json";
                res.Content.Dispose();
                res.Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType);
            }

            bool retryable = statusCode == 429 || statusCode >= 500;
            // 500s are server-side errors (e.g. Epic's backend timeout on specific pages).
            // They should NOT count toward the adaptive limiter's error rate because they
            // don't indicate we're overloading the server — only 429 (rate limit) should.
            bool countsAsLimiterFailure = statusCode == 429;

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
                if (countsAsLimiterFailure) limiter?.ReportFailure();
                res.Dispose();
                continue;
            }

            // Non-retryable status or status-code retries exhausted — let caller decide.
            // Report failure only for retryable codes that exhausted retries;
            // non-retryable codes (400, 403, 404, …) are not "failures" for
            // the adaptive limiter (the server handled the request properly).
            if (countsAsLimiterFailure)
                limiter?.ReportFailure();

            return res;
        }
        }
        finally
        {
            _inflight.TryRemove(op.OperationId, out _);
        }
    }

    /// <summary>
    /// Launch a background CDN probe if one isn't already running.
    /// The probe walks the backoff schedule, sending one HTTP request per interval.
    /// When the CDN clears (non-CDN response), it signals <see cref="_cdnResolved"/>.
    /// Callers should await <see cref="WaitForCdnClearAsync"/> after catching
    /// <see cref="CdnBlockedException"/> to wait for the probe to succeed.
    /// </summary>
    private void LaunchCdnProbe(
        Func<HttpRequestMessage> requestFactory,
        AdaptiveConcurrencyLimiter? limiter,
        string? label,
        CancellationToken ct)
    {
        // Only one probe at a time — integer CAS gate (Fix 2).
        // Use an integer primitive rather than SemaphoreSlim so ResetCdnState can
        // force-release it without risking deadlock if the prior probe is
        // wedged/abandoned. _ = ct suppresses unused-parameter warning: the caller
        // token intentionally does NOT flow into the probe (Fix 1 — decoupling).
        _ = ct;
        if (Interlocked.CompareExchange(ref _probeRunning, 1, 0) != 0)
            return; // probe already running

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cdnResolved = tcs;

        // Probe-scoped CTS linked to the executor lifetime only — NOT the caller's ct.
        // ResetCdnState cancels this CTS to abort a wedged probe.
        var probeCts = CancellationTokenSource.CreateLinkedTokenSource(_executorLifetime);
        _probeCts = probeCts;
        var probeToken = probeCts.Token;

        _probeTask = Task.Run(async () =>
        {
            var delays = CdnRetryDelaysOverride ?? DefaultCdnRetryDelays;
            try
            {
                for (int i = _cdnRetryIndex; i < MaxCdnRetries; i++)
                {
                    _cdnRetryIndex = i + 1;
                    var delay = i < delays.Length ? delays[i] : delays[^1];

                    _cdnCooldownUntil = DateTimeOffset.UtcNow + delay;

                    _log.LogWarning(
                        "CDN probe for {Operation} (attempt {CdnAttempt}/{MaxRetries}), waiting {Delay:F1}s",
                        label ?? "request", i + 1, MaxCdnRetries, delay.TotalSeconds);

                    OnCdnProbeEvent?.Invoke(new CdnProbeEvent(CdnProbeState.Waiting, i + 1, MaxCdnRetries, delay.TotalSeconds));

                    await Task.Delay(delay, probeToken);

                    // Only acquire a rate token — NOT a DOP slot
                    await (limiter?.AcquireRateTokenAsync(probeToken) ?? Task.CompletedTask);

                    OnCdnProbeEvent?.Invoke(new CdnProbeEvent(CdnProbeState.Probing, i + 1, MaxCdnRetries, 0));

                    // Per-attempt send timeout (Fix 1). Even if _http has
                    // Timeout.InfiniteTimeSpan and routes through a wedged proxy,
                    // this bounds one probe attempt to _probeSendTimeout.
                    using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(probeToken);
                    sendCts.CancelAfter(_probeSendTimeout);

                    HttpResponseMessage res;
                    try
                    {
                        Interlocked.Increment(ref _cdnProbeAttempts);
                        Interlocked.Increment(ref _totalHttpSends);
                        res = await _http.SendAsync(requestFactory(), sendCts.Token);
                    }
                    catch (HttpRequestException ex)
                    {
                        _log.LogWarning("CDN probe HTTP error (attempt {CdnAttempt}): {Error}", i + 1, ex.Message);
                        continue;
                    }
                    catch (OperationCanceledException) when (!probeToken.IsCancellationRequested)
                    {
                        // Per-attempt send timeout fired; probe itself is healthy.
                        _log.LogWarning(
                            "CDN probe send timed out after {Timeout:F1}s (attempt {CdnAttempt})",
                            _probeSendTimeout.TotalSeconds, i + 1);
                        continue;
                    }

                    // Check if CDN cleared (any non-CDN response)
                    bool isCdnBlock = false;
                    if ((int)res.StatusCode == 403)
                    {
                        var body = await res.Content.ReadAsStringAsync(probeToken);
                        isCdnBlock = !body.TrimStart().StartsWith('{');
                        if (!isCdnBlock)
                            res.Dispose(); // JSON 403 — CDN is clear
                    }

                    if (isCdnBlock)
                    {
                        res.Dispose();
                        continue; // still blocked
                    }

                    // CDN cleared
                    Interlocked.Increment(ref _cdnProbeSuccesses);
                    _cdnCooldownUntil = default;
                    _cdnRetryIndex = 0;
                    res.Dispose();
                    _log.LogWarning(
                        "CDN cleared after {ProbeAttempt} probes (total sends: {TotalSends}, blocks: {Blocks})",
                        i + 1, TotalHttpSends, CdnBlocksDetected);
                    OnCdnProbeEvent?.Invoke(new CdnProbeEvent(CdnProbeState.Cleared, i + 1, MaxCdnRetries, 0));
                    tcs.TrySetResult(true);
                    return;
                }

                // Exhausted retries
                _log.LogError("CDN probe gave up after {MaxRetries} retries.", MaxCdnRetries);
                _cdnCooldownUntil = default;
                _cdnRetryIndex = 0;
                OnCdnProbeEvent?.Invoke(new CdnProbeEvent(CdnProbeState.Exhausted, MaxCdnRetries, MaxCdnRetries, 0));
                tcs.TrySetResult(false);
            }
            catch (OperationCanceledException)
            {
                // Probe was cancelled (ResetCdnState or executor shutdown).
                // Resolve the TCS with `false` rather than TrySetCanceled so waiters
                // in WaitForCdnClearAsync wake normally instead of propagating OCE
                // up through WithCdnResilienceAsync (which would break resilience
                // loops even though the caller's own ct was never cancelled).
                tcs.TrySetResult(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "CDN probe failed unexpectedly.");
                tcs.TrySetResult(false);
            }
            finally
            {
                // Release the gate and clear the CTS ref before disposal so a
                // concurrent ResetCdnState doesn't touch a disposed object.
                _probeCts = null;
                Interlocked.Exchange(ref _probeRunning, 0);
                probeCts.Dispose();
            }
        }, probeToken);
    }

    /// <summary>
    /// Reset CDN cooldown state. Call at the start of each scrape pass to prevent
    /// stale state from a previous pass imposing unnecessarily long cooldowns.
    ///
    /// <para>If a probe is currently in flight (including one wedged on an
    /// indefinite HTTP send), this method cancels the probe's scoped CTS, releases
    /// the probe gate, resolves any outstanding waiters with a non-cancelled
    /// completion, and arms a short cooldown floor (<see cref="ResetCooldownFloor"/>)
    /// to prevent a hot-loop of callers racing to send requests before the CDN
    /// state has fully cleared.</para>
    /// </summary>
    public void ResetCdnState()
    {
        // Snapshot before mutating so concurrent probe finally-block doesn't
        // double-dispose or see a stale reference.
        var cts = _probeCts;
        var prevResolved = _cdnResolved;
        bool hadActiveProbe = Volatile.Read(ref _probeRunning) == 1;

        _cdnRetryIndex = 0;
        _cdnResolved = null;

        // Arm a short cooldown floor when abandoning an active probe (Fix 4).
        // Without this, callers racing between ResetCdnState and the first
        // post-reset 403 would see IsCdnBlocked==false, send, get CDN-blocked,
        // and (because the gate was just released) launch a probe — but the
        // window is still a hot-loop hazard. A 1s floor keeps it cool.
        _cdnCooldownUntil = hadActiveProbe
            ? DateTimeOffset.UtcNow + ResetCooldownFloor
            : default;

        // Cancel the in-flight probe. The probe's OCE handler will resolve the
        // old TCS with `false` (not TrySetCanceled) so waiters wake normally.
        if (cts is not null)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* probe already completed */ }
        }

        // Belt-and-braces: if the prior TCS somehow never gets resolved by the
        // probe (e.g. probe task crashed before its finally), resolve it here so
        // waiters don't hang. TrySetResult is a no-op if already completed.
        prevResolved?.TrySetResult(false);

        // Force-release the gate. If the probe's finally runs later and calls
        // Interlocked.Exchange(ref _probeRunning, 0) again, that's a harmless
        // no-op — the value is already 0.
        Interlocked.Exchange(ref _probeRunning, 0);
    }

    /// <summary>Short cooldown armed after <see cref="ResetCdnState"/> when an
    /// active probe is being abandoned, to avoid a hot-loop of CDN-blocked
    /// requests racing through the freshly-cleared gate.</summary>
    internal static readonly TimeSpan ResetCooldownFloor = TimeSpan.FromSeconds(1);

    /// <summary>True iff a background CDN probe is currently in flight.
    /// Test-only visibility for verifying gate lifecycle.</summary>
    internal bool IsProbeRunning => Volatile.Read(ref _probeRunning) == 1;

    /// <summary>
    /// True when a CDN block is currently active (probe in progress or cooldown pending).
    /// Callers should check this <em>before</em> acquiring a DOP slot to avoid
    /// acquiring a slot only to immediately enter CDN retry and hold it indefinitely.
    /// </summary>
    public bool IsCdnBlocked =>
        _cdnResolved is { Task.IsCompleted: false } ||
        _cdnCooldownUntil > DateTimeOffset.UtcNow;

    /// <summary>
    /// Wait until any active CDN block clears (probe resolves or cooldown expires).
    /// Returns immediately if no CDN block is active. Does not acquire any DOP or rate slots.
    /// </summary>
    public async Task WaitForCdnClearAsync(CancellationToken ct)
    {
        // Wait for active probe to resolve
        var resolved = _cdnResolved;
        if (resolved is not null && !resolved.Task.IsCompleted)
        {
            using var reg = ct.Register(() => resolved.TrySetCanceled(ct));
            await resolved.Task.ConfigureAwait(false); // ignores true/false result — just waits
        }

        // Wait for any remaining cooldown
        await WaitForCdnCooldownAsync(ct);
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

    /// <summary>
    /// Execute <paramref name="work"/> with CDN resilience: pre-wait for any active CDN block,
    /// acquire a concurrency slot, run the work, release the slot, and retry transparently
    /// on <see cref="CdnBlockedException"/>. Callers do not need to handle CDN blocks —
    /// this method catches them, releases the slot, waits for the probe to clear, and retries.
    /// </summary>
    /// <param name="work">The async operation to execute (e.g. an HTTP call).</param>
    /// <param name="ct">Cancellation token used for CDN wait and loop cancellation.</param>
    /// <param name="acquireSlot">Optional async delegate to acquire a DOP/rate slot before work.</param>
    /// <param name="releaseSlot">Optional delegate to release the DOP/rate slot after work or on CDN block.</param>
    /// <returns>The result of <paramref name="work"/> once it succeeds.</returns>
    public async Task<T> WithCdnResilienceAsync<T>(
        Func<Task<T>> work,
        CancellationToken ct,
        Func<Task>? acquireSlot = null,
        Action? releaseSlot = null)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await WaitForCdnClearAsync(ct);

            bool acquired = false;
            try
            {
                if (acquireSlot is not null)
                {
                    await acquireSlot();
                    acquired = true;
                }

                var result = await work();

                // Release slot before returning so caller does post-processing outside the slot
                if (acquired) { releaseSlot?.Invoke(); acquired = false; }
                return result;
            }
            catch (CdnBlockedException)
            {
                if (acquired) { releaseSlot?.Invoke(); acquired = false; }
                await WaitForCdnClearAsync(ct);
                // Loop back to pre-wait + re-acquire + retry
            }
            finally
            {
                // Safety: release slot if still held (e.g. non-CDN exception from work)
                if (acquired) releaseSlot?.Invoke();
            }
        }
    }
}
