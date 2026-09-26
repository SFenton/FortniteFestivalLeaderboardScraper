using System.Diagnostics;
using System.ComponentModel;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text;

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

internal sealed class ResponseBodyLimitExceededException
    : HttpRequestException
{
    internal ResponseBodyLimitExceededException()
        : base("HTTP response exceeded the configured byte limit.")
    {
    }
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

public readonly record struct HttpSendTelemetry(
    long TotalHttpSends,
    long ProbeHttpSends,
    long ProbeSuccesses,
    long StatusRetries,
    long NetworkErrors,
    long CdnBlocksDetected)
{
    public bool HasAny =>
        TotalHttpSends != 0
        || ProbeHttpSends != 0
        || ProbeSuccesses != 0
        || StatusRetries != 0
        || NetworkErrors != 0
        || CdnBlocksDetected != 0;

    public HttpSendTelemetry Since(HttpSendTelemetry baseline) =>
        new(
            TotalHttpSends - baseline.TotalHttpSends,
            ProbeHttpSends - baseline.ProbeHttpSends,
            ProbeSuccesses - baseline.ProbeSuccesses,
            StatusRetries - baseline.StatusRetries,
            NetworkErrors - baseline.NetworkErrors,
            CdnBlocksDetected - baseline.CdnBlocksDetected);
}

/// <summary>Coarse-grained state of an in-flight <see cref="ResilientHttpExecutor.SendAsync"/> call.
/// Used by the <c>/api/diag/inflight</c> endpoint to diagnose stuck scrapes.</summary>
public enum InflightState
{
    /// <summary>Created and registered, but no asynchronous wait/send has started yet.</summary>
    Created,
    /// <summary>Awaiting foreground-registration traffic admission.</summary>
    WaitingForTrafficTurn,
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
        _state = (int)InflightState.Created;
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
    private static readonly TimeSpan MaximumCancellableDelay = TimeSpan.FromDays(1);

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
    internal Action? ResetBeforeReopenLaunches { get; set; }

    /// <summary>Maximum jitter (ms) added before non-probe CDN retry attempts.
    /// Set to 0 in tests for determinism.</summary>
    internal int MaxJitterMs { get; set; } = 500;

    internal Func<HttpRequestMessage, string?, CancellationToken, Task<HttpResponseMessage?>>? CdnBlockFallbackOverride { get; set; }
    internal Func<HttpRequestMessage, string?, CancellationToken, Task<HttpResponseMessage?>>? PrimaryCurlTransportOverride { get; set; }
    internal string? CurlFallbackTempDirectory { get; set; }
    internal long? CurlResponseMaximumBytes { get; set; }
    internal Action<string>? CurlScratchValidator { get; set; }

    private readonly HttpClient _http;
    private readonly ILogger _log;
    private readonly EpicTrafficCoordinator? _trafficCoordinator;
    private readonly IProxyHealthReporter? _proxyHealth;

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

    internal static class CurlHttpFallback
    {
        public static async Task<HttpResponseMessage?> SendAsync(
            HttpRequestMessage request,
            string? label,
            TimeSpan timeout,
            ILogger log,
            CancellationToken ct,
            string? tempDirectory = null,
            bool primaryTransport = false,
            long? maximumResponseBytes = null,
            Action<string>? scratchValidator = null)
        {
            if (request.RequestUri is null)
                return null;

            var scratchRoot = string.IsNullOrWhiteSpace(tempDirectory)
                ? Path.GetTempPath()
                : Path.GetFullPath(tempDirectory);
            scratchValidator?.Invoke(scratchRoot);
            Directory.CreateDirectory(scratchRoot);
            var requestBodyPath = Path.Combine(scratchRoot, $"fst-curl-request-{Guid.NewGuid():N}.bin");
            var responseBodyPath = Path.Combine(scratchRoot, $"fst-curl-response-{Guid.NewGuid():N}.bin");
            try
            {
                if (request.Content is not null)
                {
                    var body = await request.Content.ReadAsByteArrayAsync(ct);
                    await File.WriteAllBytesAsync(requestBodyPath, body, ct);
                }

                var config = BuildCurlConfig(
                    request,
                    requestBodyPath,
                    responseBodyPath,
                    timeout,
                    maximumResponseBytes);
                using var process = new Process
                {
                    StartInfo =
                        CreateProcessStartInfo(),
                };

                try
                {
                    process.Start();
                }
                catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
                {
                    log.LogWarning("curl fallback unavailable for {Operation}: {Error}", label ?? "request", ex.Message);
                    return null;
                }

                await process.StandardInput.WriteAsync(config);
                process.StandardInput.Close();

                string stdout;
                string stderr;
                try
                {
                    var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
                    var stderrTask = process.StandardError.ReadToEndAsync(ct);
                    await WaitForExitAsync(
                        process,
                        responseBodyPath,
                        maximumResponseBytes,
                        ct);
                    stdout = await stdoutTask;
                    stderr = await stderrTask;
                }
                catch (OperationCanceledException)
                {
                    TryKill(process);
                    throw;
                }

                if (process.ExitCode == 63 &&
                    maximumResponseBytes is > 0)
                {
                    throw new ResponseBodyLimitExceededException();
                }
                if (process.ExitCode != 0)
                {
                    var error = SanitizeCurlError(stderr);
                    log.LogWarning(
                        "curl fallback failed for {Operation} with exit code {ExitCode}: {Error}",
                        label ?? "request",
                        process.ExitCode,
                        error);
                    throw new HttpRequestException($"curl fallback exited {process.ExitCode}: {error}");
                }

                var lines = stdout.Split(
                    '\n',
                    StringSplitOptions
                        .RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries);
                var statusText = lines
                    .FirstOrDefault(static line =>
                        line.StartsWith(
                            "fst-status:",
                            StringComparison.Ordinal));
                if (statusText is null ||
                    !int.TryParse(
                        statusText["fst-status:".Length..],
                        out var statusCode) ||
                    statusCode <= 0)
                {
                    log.LogWarning("curl fallback returned an invalid status for {Operation}.", label ?? "request");
                    throw new HttpRequestException("curl fallback returned an invalid status.");
                }

                byte[] responseBody;
                if (File.Exists(responseBodyPath))
                {
                    var responseLength =
                        new FileInfo(responseBodyPath).Length;
                    if (maximumResponseBytes is > 0 &&
                        responseLength >
                            maximumResponseBytes.Value)
                    {
                        throw new
                            ResponseBodyLimitExceededException();
                    }
                    responseBody =
                        await File.ReadAllBytesAsync(
                            responseBodyPath,
                            ct);
                }
                else
                {
                    responseBody = [];
                }
                var response = new HttpResponseMessage((System.Net.HttpStatusCode)statusCode)
                {
                    RequestMessage = request,
                    Content = new ByteArrayContent(responseBody),
                };

                var contentType = lines
                    .FirstOrDefault(static line =>
                        line.StartsWith(
                            "fst-content-type:",
                            StringComparison.Ordinal))?
                    ["fst-content-type:".Length..];
                if (!string.IsNullOrWhiteSpace(contentType))
                    response.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                var retryAfterText = lines
                    .FirstOrDefault(static line =>
                        line.StartsWith(
                            "fst-retry-after:",
                            StringComparison.Ordinal))?
                    ["fst-retry-after:".Length..];
                if (!string.IsNullOrWhiteSpace(
                        retryAfterText) &&
                    RetryConditionHeaderValue.TryParse(
                        retryAfterText,
                        out var retryAfter))
                {
                    response.Headers.RetryAfter =
                        retryAfter;
                }

                if (primaryTransport)
                {
                    log.LogDebug(
                        "curl primary transport returned {StatusCode} for {Operation}.",
                        statusCode,
                        label ?? "request");
                }
                else
                {
                    log.LogWarning(
                        "curl fallback returned {StatusCode} for {Operation} after .NET HTTP was CDN-blocked.",
                        statusCode,
                        label ?? "request");
                }
                return response;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not HttpRequestException)
            {
                SafeLogCurlFallbackUnexpected(log, label, ex);
                return null;
            }
            finally
            {
                TryDelete(requestBodyPath);
                TryDelete(responseBodyPath);
            }
        }

        private static void SafeLogCurlFallbackUnexpected(ILogger log, string? label, Exception ex)
        {
            try
            {
                var message = ex.Message;
                if (message.Length > 300)
                    message = string.Concat(message.AsSpan(0, 300), "...");

                log.LogWarning(
                    "curl fallback failed unexpectedly for {Operation}: {ExceptionType}: {Error}",
                    label ?? "request",
                    ex.GetType().Name,
                    message);
            }
            catch (OutOfMemoryException)
            {
                // Avoid turning best-effort fallback logging into the scrape-failing exception.
            }
        }

        internal static string BuildCurlConfig(
            HttpRequestMessage request,
            string requestBodyPath,
            string responseBodyPath,
            TimeSpan timeout,
            long? maximumResponseBytes)
        {
            var sb = new StringBuilder();
            AppendOption(sb, "silent");
            AppendOption(sb, "show-error");
            AppendOption(sb, "http1.1");
            AppendOption(sb, "compressed");
            AppendOption(sb, "max-time", Math.Max(1, timeout.TotalSeconds).ToString("F0", System.Globalization.CultureInfo.InvariantCulture));
            if (maximumResponseBytes is > 0)
            {
                AppendOption(
                    sb,
                    "max-filesize",
                    maximumResponseBytes.Value
                        .ToString(
                            System.Globalization
                                .CultureInfo
                                .InvariantCulture));
            }
            AppendOption(sb, "request", request.Method.Method);
            AppendOption(sb, "url", request.RequestUri!.ToString());
            AppendOption(sb, "output", responseBodyPath);
            AppendOption(
                sb,
                "write-out",
                "\nfst-status:%{http_code}\n" +
                "fst-content-type:%{content_type}\n" +
                "fst-retry-after:%header{retry-after}\n");

            if (request.Options.TryGetValue(ProxyRequestState.EndpointProxyUri, out var proxyUri))
                AppendOption(sb, "proxy", proxyUri.ToString());

            foreach (var header in request.Headers)
                AppendHeader(sb, header.Key, header.Value);

            if (request.Content is not null)
            {
                foreach (var header in request.Content.Headers)
                    AppendHeader(sb, header.Key, header.Value);

                AppendOption(sb, "data-binary", $"@{requestBodyPath}");
            }

            return sb.ToString();
        }

        internal static ProcessStartInfo
            CreateProcessStartInfo()
        {
            var startInfo =
                new ProcessStartInfo
                {
                    FileName = "curl",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
            startInfo.ArgumentList.Add("--disable");
            startInfo.ArgumentList.Add("--config");
            startInfo.ArgumentList.Add("-");
            return startInfo;
        }

        private static async Task WaitForExitAsync(
            Process process,
            string responseBodyPath,
            long? maximumResponseBytes,
            CancellationToken ct)
        {
            var wait = process.WaitForExitAsync(ct);
            if (maximumResponseBytes is not > 0)
            {
                await wait;
                return;
            }

            while (!wait.IsCompleted)
            {
                var completed = await Task.WhenAny(
                    wait,
                    Task.Delay(
                        TimeSpan.FromMilliseconds(50),
                        ct));
                if (completed == wait)
                    break;
                if (File.Exists(responseBodyPath) &&
                    new FileInfo(responseBodyPath).Length >
                        maximumResponseBytes.Value)
                {
                    TryKill(process);
                    try
                    {
                        await process.WaitForExitAsync(
                            CancellationToken.None);
                    }
                    catch
                    {
                    }
                    throw new ResponseBodyLimitExceededException();
                }
            }
            await wait;
        }

        private static void AppendHeader(StringBuilder sb, string name, IEnumerable<string> values)
        {
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            AppendOption(sb, "header", $"{name}: {string.Join(", ", values)}");
        }

        private static void AppendOption(StringBuilder sb, string name)
            => sb.Append(name).Append('\n');

        private static void AppendOption(StringBuilder sb, string name, string value)
            => sb.Append(name).Append(" = \"").Append(EscapeCurlConfig(value)).Append("\"\n");

        private static string EscapeCurlConfig(string value)
            => value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("\t", "\\t", StringComparison.Ordinal);

        private static string SanitizeCurlError(string error)
        {
            error = error.Trim();
            return error.Length <= 300 ? error : error[..300];
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ── CDN wire diagnostics ──────────────────────────────────
    private long _cdnBlocksDetected;
    private long _cdnProbeAttempts;
    private long _cdnProbeSuccesses;
    private long _totalHttpSends;
    private long _statusRetries;
    private long _networkErrors;
    private readonly AsyncLocal<HttpSendTelemetryScope?> _telemetryScope = new();

    /// <summary>Number of times a CDN block (403 non-JSON) was detected.</summary>
    public long CdnBlocksDetected => Volatile.Read(ref _cdnBlocksDetected);
    /// <summary>Number of probe HTTP sends during CDN retry sequences.</summary>
    public long CdnProbeAttempts => Volatile.Read(ref _cdnProbeAttempts);
    /// <summary>Number of probe attempts that returned a non-CDN response (CDN cleared).</summary>
    public long CdnProbeSuccesses => Volatile.Read(ref _cdnProbeSuccesses);
    /// <summary>Total HTTP sends (including probes, retries, everything).</summary>
    public long TotalHttpSends => Volatile.Read(ref _totalHttpSends);
    public long StatusRetries => Volatile.Read(ref _statusRetries);
    public long NetworkErrors => Volatile.Read(ref _networkErrors);

    internal HttpSendTelemetry CaptureTelemetry() =>
        new(
            TotalHttpSends,
            CdnProbeAttempts,
            CdnProbeSuccesses,
            StatusRetries,
            NetworkErrors,
            CdnBlocksDetected);

    internal TelemetryScope BeginTelemetryScope()
    {
        var scope = new HttpSendTelemetryScope(_telemetryScope.Value);
        _telemetryScope.Value = scope;
        return new TelemetryScope(this, scope);
    }

    internal async Task<(T Result, long HttpSends)> MeasureHttpSendsAsync<T>(
        Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var scope = BeginTelemetryScope();
        var result = await operation().ConfigureAwait(false);
        return (result, scope.Snapshot().TotalHttpSends);
    }

    internal sealed class TelemetryScope : IDisposable
    {
        private readonly ResilientHttpExecutor _owner;
        private readonly HttpSendTelemetryScope _scope;
        private bool _disposed;

        internal TelemetryScope(
            ResilientHttpExecutor owner,
            HttpSendTelemetryScope scope)
        {
            _owner = owner;
            _scope = scope;
        }

        internal HttpSendTelemetry Snapshot() => _scope.Snapshot();

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _owner._telemetryScope.Value = _scope.Parent;
        }
    }

    internal sealed class HttpSendTelemetryScope
    {
        internal HttpSendTelemetryScope? Parent { get; }
        internal long TotalHttpSends;
        internal long ProbeHttpSends;
        internal long ProbeSuccesses;
        internal long StatusRetries;
        internal long NetworkErrors;
        internal long CdnBlocksDetected;

        internal HttpSendTelemetryScope(HttpSendTelemetryScope? parent)
            => Parent = parent;

        internal void RecordSend(bool isProbe)
        {
            Interlocked.Increment(ref TotalHttpSends);
            if (isProbe)
                Interlocked.Increment(ref ProbeHttpSends);
        }

        internal HttpSendTelemetry Snapshot() => new(
            Volatile.Read(ref TotalHttpSends),
            Volatile.Read(ref ProbeHttpSends),
            Volatile.Read(ref ProbeSuccesses),
            Volatile.Read(ref StatusRetries),
            Volatile.Read(ref NetworkErrors),
            Volatile.Read(ref CdnBlocksDetected));
    }

    private void ForEachTelemetryScope(Action<HttpSendTelemetryScope> action)
    {
        for (var scope = _telemetryScope.Value; scope is not null; scope = scope.Parent)
            action(scope);
    }

    private void RecordHttpSend(bool isProbe = false)
    {
        Interlocked.Increment(ref _totalHttpSends);
        ForEachTelemetryScope(scope => scope.RecordSend(isProbe));
    }

    private void RecordStatusRetry()
    {
        Interlocked.Increment(ref _statusRetries);
        ForEachTelemetryScope(scope => Interlocked.Increment(ref scope.StatusRetries));
    }

    private void RecordNetworkError()
    {
        Interlocked.Increment(ref _networkErrors);
        ForEachTelemetryScope(scope => Interlocked.Increment(ref scope.NetworkErrors));
    }

    private void RecordCdnBlock()
    {
        Interlocked.Increment(ref _cdnBlocksDetected);
        ForEachTelemetryScope(scope => Interlocked.Increment(ref scope.CdnBlocksDetected));
    }

    private void RecordProbeSuccess()
    {
        Interlocked.Increment(ref _cdnProbeSuccesses);
        ForEachTelemetryScope(scope => Interlocked.Increment(ref scope.ProbeSuccesses));
    }

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
    // integer primitive (not a SemaphoreSlim); reset waits for the owning probe
    // to release it before a future probe can launch.
    private DateTimeOffset _cdnCooldownUntil;
    private int _probeRunning; // 0 = no probe, 1 = probe in flight
    private int _cdnRetryIndex; // current position in the delay schedule
    private volatile TaskCompletionSource<bool>? _cdnResolved; // true=CDN clear, false=gave up
    private Task? _probeTask; // background probe task (fire-and-forget with TCS signal)
    private volatile CancellationTokenSource? _probeCts; // scoped to in-flight probe; null when idle
    private readonly object _resetLock = new();
    private TaskCompletionSource<bool>? _resetCompletion;
    private int _resetInProgress;

    // ── Probe bounds & lifetime (Fix 1) ───────────────────────
    /// <summary>Default per-attempt timeout for probe HTTP sends. Prevents a single
    /// probe send from hanging indefinitely (e.g. on a wedged proxy connection).</summary>
    public static readonly TimeSpan DefaultProbeSendTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Default per-attempt wall-clock timeout for caller <see cref="SendAsync"/>
    /// invocations. Since <c>HttpClient.Timeout</c> is typically set to
    /// <see cref="Timeout.InfiniteTimeSpan"/> (to let this executor govern retries),
    /// this bound prevents a single wedged TCP connection (e.g. zombie socket after
    /// a proxy VPN recycle) from hanging a request indefinitely. On timeout the send
    /// is treated as a transient network error and retried indefinitely — callers see
    /// real service errors or success, never a timeout.</summary>
    public static readonly TimeSpan DefaultSendWallClockTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _probeSendTimeout;
    private readonly TimeSpan _sendWallClockTimeout;
    private readonly CancellationToken _executorLifetime;

    public ResilientHttpExecutor(HttpClient http, ILogger log)
        : this(http, log, probeSendTimeout: null, sendWallClockTimeout: null, executorLifetime: default) { }

    public ResilientHttpExecutor(HttpClient http, ILogger log, EpicTrafficCoordinator trafficCoordinator)
        : this(http, log, probeSendTimeout: null, sendWallClockTimeout: null, executorLifetime: default, trafficCoordinator) { }

    public ResilientHttpExecutor(HttpClient http, ILogger log, IProxyHealthReporter? proxyHealth)
        : this(http, log, probeSendTimeout: null, sendWallClockTimeout: null, executorLifetime: default, trafficCoordinator: null, proxyHealth) { }

    public ResilientHttpExecutor(
        HttpClient http,
        ILogger log,
        EpicTrafficCoordinator? trafficCoordinator,
        IProxyHealthReporter? proxyHealth)
        : this(http, log, probeSendTimeout: null, sendWallClockTimeout: null, executorLifetime: default, trafficCoordinator, proxyHealth) { }

    /// <param name="probeSendTimeout">Per-attempt timeout for the background CDN probe
    /// send. Defaults to <see cref="DefaultProbeSendTimeout"/>. A single probe
    /// <c>HttpClient.SendAsync</c> call will not block longer than this, regardless of
    /// the underlying client's <c>Timeout</c>.</param>
    /// <param name="sendWallClockTimeout">Per-attempt wall-clock timeout for caller
    /// <see cref="SendAsync"/> invocations. Defaults to
    /// <see cref="DefaultSendWallClockTimeout"/>. On timeout the send is counted as a
    /// transient network error and the retry loop continues — the caller will not
    /// observe a timeout unless they cancel themselves.</param>
    /// <param name="executorLifetime">Token tied to the executor's owner (typically
    /// <c>IHostApplicationLifetime.ApplicationStopping</c>). The background probe uses
    /// this token for outer delays and as the base of its per-attempt send token, so
    /// the probe is decoupled from any individual caller's cancellation token.</param>
    public ResilientHttpExecutor(
        HttpClient http,
        ILogger log,
        TimeSpan? probeSendTimeout,
        TimeSpan? sendWallClockTimeout,
        CancellationToken executorLifetime,
        EpicTrafficCoordinator? trafficCoordinator = null,
        IProxyHealthReporter? proxyHealth = null)
    {
        _http = http;
        _log = log;
        _trafficCoordinator = trafficCoordinator;
        _proxyHealth = proxyHealth;
        _probeSendTimeout = probeSendTimeout ?? DefaultProbeSendTimeout;
        _sendWallClockTimeout = sendWallClockTimeout ?? DefaultSendWallClockTimeout;
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

                if (_trafficCoordinator is not null && !_trafficCoordinator.CurrentRequestCanBypassBackgroundGate)
                {
                    op.SetState(InflightState.WaitingForTrafficTurn);
                    await _trafficCoordinator.WaitForTurnAsync(ct);
                }

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

                using var sentRequest = requestFactory();
                HttpResponseMessage res;
                // Per-attempt wall-clock deadline: HttpClient.Timeout is typically Infinite
                // so a wedged connection (zombie TCP after proxy recycle) can hang forever.
                // Linked CTS fires at _sendWallClockTimeout; the resulting TaskCanceledException
                // is caught below (ct.IsCancellationRequested is false) and counted as a transient
                // network error, so the retry loop continues until real success or service error.
                using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                sendCts.CancelAfter(_sendWallClockTimeout);
                try
                {
                    op.SetState(InflightState.Sending);
                    if (_proxyHealth is ProxyPool { UseCurlTransport: true } proxyPool)
                    {
                        using var proxyLease = await proxyPool.AcquireAsync(sendCts.Token)
                            ?? throw new InvalidOperationException(
                                "Curl proxy transport requires at least one configured endpoint.");
                        proxyLease.Apply(sentRequest);
                        proxyPool.PrepareRequest(sentRequest);
                        RecordHttpSend();

                        res = PrimaryCurlTransportOverride is not null
                            ? await PrimaryCurlTransportOverride(sentRequest, label, sendCts.Token)
                                ?? throw new HttpRequestException("curl primary transport returned no response")
                            : await CurlHttpFallback.SendAsync(
                                sentRequest,
                                label,
                                _sendWallClockTimeout,
                                _log,
                                sendCts.Token,
                                proxyPool.CurlTempDirectory,
                                primaryTransport: true,
                                maximumResponseBytes:
                                    CurlResponseMaximumBytes,
                                scratchValidator:
                                    CurlScratchValidator)
                                ?? throw new HttpRequestException("curl primary transport returned no response");
                    }
                    else
                    {
                        PrepareWireSendCounting(sentRequest);
                        res = await _http.SendAsync(sentRequest, sendCts.Token);
                    }
                }
                catch (ResponseBodyLimitExceededException)
                {
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    RecordNetworkError();
                    if (IsCdnBlocked)
                        throw new CdnBlockedException(
                            $"CDN block on {label ?? "request"} (network error during CDN block: {ex.Message})");

                    var fallbackResponse = await TrySendAfterTransportFailureAsync(
                        sentRequest,
                        label,
                        limiter,
                        ct);
                    if (fallbackResponse is not null)
                    {
                        res = fallbackResponse;
                    }
                    else
                    {
                        networkErrors++;
                        _log.LogWarning(
                            "HTTP error for {Operation} (networkError {NetErr}, DOP {Dop}): {Error}",
                            label ?? "request", networkErrors, limiter?.CurrentDop ?? -1, ex.Message);
                        limiter?.ReportFailure();
                        _proxyHealth?.ReportFailure(sentRequest, ProxyFailureKind.Transport);
                        continue; // transient — retry indefinitely
                    }
                }
                catch (ObjectDisposedException ex) when (!ct.IsCancellationRequested)
                {
                    RecordNetworkError();
                    // SocketsHttpHandler connection pool reset mid-send (e.g. proxy rotation
                    // forcing ResetConnectionPool) can surface as ObjectDisposedException.
                    // Treat as transient and retry indefinitely.
                    if (IsCdnBlocked)
                        throw new CdnBlockedException(
                            $"CDN block on {label ?? "request"} (disposed during CDN block: {ex.Message})");

                    var fallbackResponse = await TrySendAfterTransportFailureAsync(
                        sentRequest,
                        label,
                        limiter,
                        ct);
                    if (fallbackResponse is not null)
                    {
                        res = fallbackResponse;
                    }
                    else
                    {
                        networkErrors++;
                        _log.LogWarning(
                            "Connection disposed for {Operation} (networkError {NetErr}, DOP {Dop}): {Error}",
                            label ?? "request", networkErrors, limiter?.CurrentDop ?? -1, ex.Message);
                        limiter?.ReportFailure();
                        _proxyHealth?.ReportFailure(sentRequest, ProxyFailureKind.Transport);
                        continue; // transient — retry indefinitely
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    RecordNetworkError();
                    if (IsCdnBlocked)
                        throw new CdnBlockedException(
                            $"CDN block on {label ?? "request"} (timeout during CDN block)");

                    var fallbackResponse = await TrySendAfterTransportFailureAsync(
                        sentRequest,
                        label,
                        limiter,
                        ct);
                    if (fallbackResponse is not null)
                    {
                        res = fallbackResponse;
                    }
                    else
                    {
                        // This covers both the legacy HttpClient.Timeout fire AND our per-attempt
                        // wall-clock deadline (sendCts.CancelAfter). Either way the caller's ct
                        // was not cancelled, so we treat as transient and retry indefinitely.
                        networkErrors++;
                        _log.LogWarning(
                            "Timeout for {Operation} (networkError {NetErr}, DOP {Dop}, wall-clock {WallClockMs}ms)",
                            label ?? "request", networkErrors, limiter?.CurrentDop ?? -1, _sendWallClockTimeout.TotalMilliseconds);
                        limiter?.ReportFailure();
                        _proxyHealth?.ReportFailure(sentRequest, ProxyFailureKind.Timeout);
                        continue; // transient timeout — retry indefinitely
                    }
                }

                var statusCode = (int)res.StatusCode;
                var disguisedCdnBlock =
                    res.IsSuccessStatusCode
                    && IsEpicEventsRequest(sentRequest)
                    && string.Equals(
                        res.Content.Headers.ContentType?.MediaType,
                        "text/html",
                        StringComparison.OrdinalIgnoreCase);

                // ── CDN block detection (403 with non-JSON body) ──────────
                // On CDN block: try the curl transport fallback first. If that does
                // not recover, isolate the failure to the selected proxy before ever
                // entering the legacy global CDN probe.
                if (statusCode == 403 || disguisedCdnBlock)
                {
                    op.SetState(InflightState.ReadingBody);
                    var body = await res.Content.ReadAsStringAsync(ct);
                    bool isCdnBlock = disguisedCdnBlock || !body.TrimStart().StartsWith('{');

                    if (isCdnBlock)
                    {
                        // Count each physical response classified as a CDN
                        // block, including the foreground response.
                        RecordCdnBlock();
                        try
                        {
                            var fallbackResponse =
                                await TrySendCdnBlockedRequestWithFallbackAsync(
                                    sentRequest,
                                    label,
                                    limiter,
                                    ct);
                            if (fallbackResponse is not null)
                            {
                                if (await IsCdnBlockResponseAsync(fallbackResponse, ct))
                                {
                                    RecordCdnBlock();
                                    fallbackResponse.Dispose();
                                }
                                else if ((int)fallbackResponse.StatusCode ==
                                             429 ||
                                         (int)fallbackResponse.StatusCode >=
                                             500)
                                {
                                    res.Dispose();
                                    res = fallbackResponse;
                                    statusCode =
                                        (int)res.StatusCode;
                                    goto ProcessStatus;
                                }
                                else
                                {
                                    res.Dispose();
                                    limiter?.ReportSuccess();
                                    _proxyHealth?.ReportSuccess(sentRequest);
                                    return fallbackResponse;
                                }
                            }
                        }
                        catch (ResponseBodyLimitExceededException)
                        {
                            res.Dispose();
                            throw;
                        }
                        catch (HttpRequestException ex)
                        {
                            res.Dispose();
                            RecordNetworkError();
                            networkErrors++;
                            _log.LogWarning(
                                "curl fallback transport error for {Operation} (networkError {NetErr}, DOP {Dop}): {Error}",
                                label ?? "request", networkErrors, limiter?.CurrentDop ?? -1, ex.Message);
                            limiter?.ReportFailure();
                            _proxyHealth?.ReportFailure(sentRequest, ProxyFailureKind.Transport);
                            continue;
                        }

                        res.Dispose();

                        var cdnDecision = ProxyCdnBlockDecision.PauseGlobally;
                        if (_proxyHealth is IProxyCdnBlockHandler cdnBlockHandler)
                        {
                            cdnDecision = cdnBlockHandler.ReportCdnBlock(sentRequest);
                        }
                        else
                        {
                            _proxyHealth?.ReportFailure(sentRequest, ProxyFailureKind.CdnBlock);
                        }

                        if (cdnDecision == ProxyCdnBlockDecision.RetryOnAlternateProxy)
                        {
                            _log.LogWarning(
                                "CDN block for {Operation} was isolated to one proxy; retrying on alternate proxy (wire sends: {TotalSends}, blocks: {Blocks}).",
                                label ?? "request", TotalHttpSends, CdnBlocksDetected);
                            continue;
                        }

                        if (cdnDecision == ProxyCdnBlockDecision.WaitForProxyCooldown)
                        {
                            _log.LogWarning(
                                "CDN block for {Operation} cooled every proxy; waiting for proxy cooldown instead of pausing globally (wire sends: {TotalSends}, blocks: {Blocks}).",
                                label ?? "request", TotalHttpSends, CdnBlocksDetected);
                            continue;
                        }

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

            ProcessStatus:
                if (res.IsSuccessStatusCode)
                {
                    limiter?.ReportSuccess();
                    _proxyHealth?.ReportSuccess(sentRequest);
                    return res;
                }

                bool retryable = statusCode == 429 || statusCode >= 500;
                // 500s are server-side errors (e.g. Epic's backend timeout on specific pages).
                // They should NOT count toward the adaptive limiter's error rate because they
                // don't indicate we're overloading the server — only 429 (rate limit) should.
                // An HTML edge 429 on a proxied request is a per-egress-IP limit
                // that a refresh-enabled pool handles by replacing that exit's
                // egress; it is not evidence of aggregate overload, so it must
                // not shrink global concurrency. JSON (possibly account-level)
                // throttles still count.
                bool perExitEdgeRateLimit = statusCode == 429
                    && IsPerExitEdgeRateLimit(sentRequest, res);
                bool countsAsLimiterFailure = statusCode == 429 && !perExitEdgeRateLimit;
                TimeSpan? retryAfter = statusCode == 429
                    ? GetPositiveRetryAfter(res)
                    : null;

                if (statusCode == 429)
                {
                    ReportRateLimited(
                        sentRequest,
                        retryAfter,
                        res.Content.Headers.ContentType?.MediaType);
                }

                if (retryable && statusAttempt < maxRetries)
                {
                    RecordStatusRetry();
                    statusAttempt++;

                    // Honour a positive Retry-After header on 429. The endpoint
                    // cooldown is reported above for every 429, including the
                    // terminal response.
                    if (statusCode == 429 && retryAfter is { } delay)
                    {
                        _log.LogWarning(
                            "Rate-limited on {Operation}, waiting {Delay:F1}s (DOP {Dop})",
                            label ?? "request", delay.TotalSeconds, limiter?.CurrentDop ?? -1);
                        if (countsAsLimiterFailure) limiter?.ReportFailure();
                        res.Dispose();
                        await DelayCancellableAsync(delay, ct);
                        continue;
                    }

                    _log.LogWarning(
                        "{StatusCode} for {Operation} (attempt {Attempt}/{MaxAttempts}, DOP {Dop})",
                        statusCode, label ?? "request", statusAttempt, maxRetries + 1, limiter?.CurrentDop ?? -1);
                    if (countsAsLimiterFailure) limiter?.ReportFailure();
                    if (statusCode != 429)
                    {
                        _proxyHealth?.ReportFailure(sentRequest, ProxyFailureKind.ServerError);
                    }
                    res.Dispose();
                    continue;
                }

                // Non-retryable status or status-code retries exhausted — let caller decide.
                // Report failure only for retryable codes that exhausted retries;
                // non-retryable codes (400, 403, 404, …) are not "failures" for
                // the adaptive limiter (the server handled the request properly).
                if (countsAsLimiterFailure)
                {
                    limiter?.ReportFailure();
                }

                return res;
            }
        }
        finally
        {
            _inflight.TryRemove(op.OperationId, out _);
        }
    }

    private bool IsPerExitEdgeRateLimit(HttpRequestMessage request, HttpResponseMessage response)
        => _proxyHealth is ProxyPool { RefreshesRateLimitedExits: true }
            && request.Options.TryGetValue(ProxyRequestState.EndpointIndex, out _)
            && response.Content.Headers.ContentType?.MediaType is { } mediaType
            && mediaType.Contains("html", StringComparison.OrdinalIgnoreCase);

    private void ReportRateLimited(
        HttpRequestMessage request,
        TimeSpan? retryAfter,
        string? mediaType)
    {
        if (_proxyHealth is IProxyRateLimitReporter rateLimitReporter)
        {
            rateLimitReporter.ReportRateLimited(request, retryAfter, mediaType);
        }
        else
        {
            _proxyHealth?.ReportFailure(request, ProxyFailureKind.RateLimited);
        }
    }

    private static TimeSpan? GetPositiveRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            return delta;

        if (retryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
                return delay;
        }

        return null;
    }

    private static async Task DelayCancellableAsync(TimeSpan delay, CancellationToken ct)
    {
        while (delay > MaximumCancellableDelay)
        {
            await Task.Delay(MaximumCancellableDelay, ct);
            delay -= MaximumCancellableDelay;
        }

        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, ct);
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
        // ResetCdnState cancels but does not force-release this gate; the owning
        // probe releases it in finally so a new probe cannot overlap an old one.
        // _ = ct suppresses unused-parameter warning: the caller
        // token intentionally does NOT flow into the probe (Fix 1 — decoupling).
        _ = ct;
        if (Volatile.Read(ref _resetInProgress) != 0
            || Interlocked.CompareExchange(ref _probeRunning, 1, 0) != 0)
            return; // probe already running

        // Reset may have started after the first check but before this probe
        // acquired the gate. Do not launch into a reset finalization window.
        if (Volatile.Read(ref _resetInProgress) != 0)
        {
            Interlocked.Exchange(ref _probeRunning, 0);
            return;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cdnResolved = tcs;

        // Probe-scoped CTS linked to the executor lifetime only — NOT the caller's ct.
        // ResetCdnState cancels this CTS to abort a wedged probe.
        var probeCts = CancellationTokenSource.CreateLinkedTokenSource(_executorLifetime);
        _probeCts = probeCts;
        var probeToken = probeCts.Token;

        // Always start the delegate so its catch/finally can resolve waiters and
        // release the gate even when the executor lifetime is already cancelled.
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

                    using var probeRequest = requestFactory();
                    HttpResponseMessage res;
                    try
                    {
                        Interlocked.Increment(ref _cdnProbeAttempts);
                        PrepareWireSendCounting(probeRequest, isProbe: true);
                        res = await _http.SendAsync(probeRequest, sendCts.Token);
                        if (res.IsSuccessStatusCode)
                            _proxyHealth?.ReportSuccess(probeRequest);
                    }

                    catch (ResponseBodyLimitExceededException)
                    {
                        throw;
                    }
                    catch (HttpRequestException ex)
                    {
                        RecordNetworkError();
                        _log.LogWarning("CDN probe HTTP error (attempt {CdnAttempt}): {Error}", i + 1, ex.Message);
                        _proxyHealth?.ReportFailure(probeRequest, ProxyFailureKind.Transport);
                        continue;
                    }
                    catch (OperationCanceledException) when (!probeToken.IsCancellationRequested)
                    {
                        RecordNetworkError();
                        // Per-attempt send timeout fired; probe itself is healthy.
                        _log.LogWarning(
                            "CDN probe send timed out after {Timeout:F1}s (attempt {CdnAttempt})",
                            _probeSendTimeout.TotalSeconds, i + 1);
                        _proxyHealth?.ReportFailure(probeRequest, ProxyFailureKind.Timeout);
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
                        RecordCdnBlock();
                        _proxyHealth?.ReportFailure(probeRequest, ProxyFailureKind.CdnBlock);
                        res.Dispose();
                        continue; // still blocked
                    }

                    // CDN cleared
                    RecordProbeSuccess();
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
                tcs.TrySetException(new CdnBlockedException(
                    $"CDN probe gave up after {MaxCdnRetries} retries."));
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
        });
    }

    private async Task<HttpResponseMessage?> TrySendCdnBlockedRequestWithFallbackAsync(
        HttpRequestMessage request,
        string? label,
        AdaptiveConcurrencyLimiter? limiter,
        CancellationToken ct)
    {
        if (!IsEpicEventsRequest(request))
            return null;

        // A configured proxy pool can isolate the failed exit and retry through
        // another proxy. Bypassing that pool with a direct curl process turns CDN
        // HTML into misleading HTTP 200 responses and adds an unnecessary wire send.
        if (_proxyHealth is ProxyPool
            {
                IsEnabled: true,
            } ||
            request.Options.TryGetValue(
                ProxyRequestState.EndpointIndex,
                out _))
            return null;

        await (limiter?.AcquireRateTokenAsync(ct) ??
            Task.CompletedTask);
        RecordHttpSend();
        if (CdnBlockFallbackOverride is not null)
            return await CdnBlockFallbackOverride(request, label, ct);

        return await CurlHttpFallback.SendAsync(
            request,
            label,
            _sendWallClockTimeout,
            _log,
            ct,
            CurlFallbackTempDirectory,
            maximumResponseBytes:
                CurlResponseMaximumBytes,
            scratchValidator:
                CurlScratchValidator);
    }

    private async Task<HttpResponseMessage?> TrySendAfterTransportFailureAsync(
        HttpRequestMessage request,
        string? label,
        AdaptiveConcurrencyLimiter? limiter,
        CancellationToken ct)
    {
        try
        {
            var fallbackResponse =
                await TrySendCdnBlockedRequestWithFallbackAsync(
                    request,
                    label,
                    limiter,
                    ct);
            if (fallbackResponse is null)
                return null;

            if (await IsCdnBlockResponseAsync(fallbackResponse, ct))
            {
                RecordCdnBlock();
                fallbackResponse.Dispose();
                return null;
            }

            if (fallbackResponse.IsSuccessStatusCode)
            {
                _log.LogWarning(
                    "curl fallback recovered {Operation} after .NET HTTP transport failure.",
                    label ?? "request");
            }
            return fallbackResponse;
        }
        catch (ResponseBodyLimitExceededException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            RecordNetworkError();
            _log.LogWarning(
                "curl fallback also failed for {Operation} after .NET HTTP transport failure: {Error}",
                label ?? "request",
                ex.Message);
            return null;
        }
    }

    private static bool IsEpicEventsRequest(HttpRequestMessage request)
        => request.RequestUri is { Host: "events-public-service-live.ol.epicgames.com" };

    private void PrepareWireSendCounting(
        HttpRequestMessage request,
        bool isProbe = false)
    {
        if (_proxyHealth is ProxyPool
            {
                IsEnabled: true,
            })
        {
            request.Options.Set(
                ProxyRequestState.WireSendRecorder,
                (Action<bool>)(_ => RecordHttpSend(isProbe)));
            return;
        }
        RecordHttpSend(isProbe);
    }

    internal ProxyCdnBlockDecision ReportMalformedSuccessResponse(
        HttpResponseMessage response,
        string? label)
    {
        if (response.RequestMessage is not { } request
            || !IsEpicEventsRequest(request))
        {
            return ProxyCdnBlockDecision.PauseGlobally;
        }

        RecordCdnBlock();
        var decision = ProxyCdnBlockDecision.PauseGlobally;
        if (_proxyHealth is IProxyCdnBlockHandler handler)
        {
            decision = handler.ReportCdnBlock(request);
        }
        else
        {
            _proxyHealth?.ReportFailure(request, ProxyFailureKind.CdnBlock);
        }

        _log.LogWarning(
            "Malformed successful response for {Operation} was classified as a CDN block; decision={Decision}.",
            label ?? "request",
            decision);
        return decision;
    }

    private static async Task<bool> IsCdnBlockResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if ((int)response.StatusCode != 403 && !response.IsSuccessStatusCode)
            return false;

        var body = await response.Content.ReadAsStringAsync(ct);
        var trimmed = body.TrimStart();
        var isJson = trimmed.StartsWith('{') || trimmed.StartsWith('[');
        if ((int)response.StatusCode == 403)
            return !isJson;

        return !isJson;
    }

    /// <summary>
    /// Reset CDN cooldown state. Call at the start of each scrape pass to prevent
    /// stale state from a previous pass imposing unnecessarily long cooldowns.
    ///
    /// <para>If a probe is currently in flight (including one wedged on an
    /// indefinite HTTP send), this method cancels the probe's scoped CTS,
    /// resolves outstanding waiters with a non-cancelled completion, and waits
    /// for the owning probe to release its gate before returning. The async
    /// scrape-boundary variant performs the same ownership-safe wait without
    /// blocking a caller thread.</para>
    /// </summary>
    public void ResetCdnState()
    {
        var (completion, owner, hadActiveProbe) = BeginReset();
        if (owner)
        {
            try
            {
                CompleteResetAsync(completion, synchronous: true, hadActiveProbe)
                    .GetAwaiter()
                    .GetResult();
            }
            catch
            {
                completion.TrySetException(new InvalidOperationException(
                    "Synchronous CDN reset failed."));
                throw;
            }
        }
        else
        {
            completion.Task.GetAwaiter().GetResult();
        }
    }

    private void ResetCdnStateCore()
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

        // Do not release the gate here. The old probe owns it until its finally
        // block runs; releasing it before that point would allow a new probe to
        // overlap the old probe and contaminate an operation-scoped snapshot.
    }

    /// <summary>Short cooldown armed after <see cref="ResetCdnState"/> when an
    /// active probe is being abandoned, to avoid a hot-loop of CDN-blocked
    /// requests racing through the freshly-cleared gate.</summary>
    internal static readonly TimeSpan ResetCooldownFloor = TimeSpan.FromSeconds(1);

    internal DateTimeOffset CdnCooldownUntilUtc => _cdnCooldownUntil;

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
        if (resolved is not null)
        {
            await resolved.Task
                .WaitAsync(ct)
                .ConfigureAwait(false);
        }

        // Wait for any remaining cooldown
        await WaitForCdnCooldownAsync(ct);
    }

    internal async Task QuiesceCdnProbeAsync(
        CancellationToken ct)
    {
        while (Volatile.Read(
                   ref _probeRunning) == 1)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                _probeCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            var probeTask =
                Volatile.Read(ref _probeTask);
            if (probeTask is null ||
                probeTask.IsCompleted)
            {
                await Task.Yield();
                continue;
            }
            await probeTask
                .WaitAsync(ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Cancels and awaits any existing CDN probe before resetting shared state.
    /// This is the safe boundary for starting a new scrape operation.
    /// </summary>
    internal async Task ResetCdnStateAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (completion, owner, _) = BeginReset();
        if (owner)
        {
            // Once ownership is acquired, always finish the safety reset. A caller
            // cancelling after this point must not reopen probe launches early.
            await CompleteResetAsync(completion, synchronous: false, hadActiveProbe: false)
                .ConfigureAwait(false);
        }
        else
        {
            await completion.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private (
        TaskCompletionSource<bool> Completion,
        bool Owner,
        bool HadActiveProbe) BeginReset()
    {
        lock (_resetLock)
        {
            if (_resetCompletion is not null)
                return (_resetCompletion, false, false);

            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _resetCompletion = completion;
            Volatile.Write(ref _resetInProgress, 1);
            var hadActiveProbe = Volatile.Read(ref _probeRunning) == 1;
            ResetCdnStateCore();
            return (completion, true, hadActiveProbe);
        }
    }

    private async Task CompleteResetAsync(
        TaskCompletionSource<bool> completion,
        bool synchronous,
        bool hadActiveProbe)
    {
        Exception? failure = null;
        try
        {
            if (synchronous)
            {
                QuiesceCdnProbeAsync(CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            else
            {
                await QuiesceCdnProbeAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }

            _cdnRetryIndex = 0;
            _cdnResolved = null;
            _cdnCooldownUntil = synchronous && hadActiveProbe
                ? DateTimeOffset.UtcNow + ResetCooldownFloor
                : default;
            ResetBeforeReopenLaunches?.Invoke();
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            lock (_resetLock)
            {
                Volatile.Write(ref _resetInProgress, 0);
                if (ReferenceEquals(_resetCompletion, completion))
                    _resetCompletion = null;
            }

            if (failure is null)
                completion.TrySetResult(true);
            else
                completion.TrySetException(failure);
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
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
            if (_trafficCoordinator is not null)
                await _trafficCoordinator.WaitForTurnAsync(ct);
            await WaitForCdnClearAsync(ct);

            bool acquired = false;
            IDisposable? admittedRequest = null;
            try
            {
                if (acquireSlot is not null)
                {
                    await acquireSlot();
                    acquired = true;
                    admittedRequest = _trafficCoordinator?.BeginAdmittedRequest();
                }

                var result = await work();

                // Release slot before returning so caller does post-processing outside the slot
                if (acquired)
                {
                    admittedRequest?.Dispose();
                    admittedRequest = null;
                    releaseSlot?.Invoke();
                    acquired = false;
                }
                return result;
            }
            catch (CdnBlockedException)
            {
                if (acquired)
                {
                    admittedRequest?.Dispose();
                    admittedRequest = null;
                    releaseSlot?.Invoke();
                    acquired = false;
                }
                await WaitForCdnClearAsync(ct);
                // Loop back to pre-wait + re-acquire + retry
            }
            finally
            {
                // Safety: release slot if still held (e.g. non-CDN exception from work)
                if (acquired)
                {
                    admittedRequest?.Dispose();
                    releaseSlot?.Invoke();
                }
            }
        }
    }
}
