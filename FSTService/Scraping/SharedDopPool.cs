using FortniteFestival.Core.Scraping;

namespace FSTService.Scraping;

/// <summary>
/// Priority-aware wrapper around a shared <see cref="AdaptiveConcurrencyLimiter"/>.
/// All V2 API calls across all <see cref="SongProcessingMachine"/> instances flow
/// through this pool, ensuring the total DOP budget is never exceeded.
///
/// <para>Two priority lanes:</para>
/// <list type="bullet">
///   <item><b>High (PostScrape):</b> Direct access to the inner limiter, up to 100% of DOP.</item>
///   <item><b>Low (Backfill):</b> Gated by a secondary semaphore that caps how many
///     low-priority callers can hold inner slots simultaneously.</item>
/// </list>
///
/// The inner <see cref="AdaptiveConcurrencyLimiter"/> handles AIMD congestion control.
/// The low-priority gate ensures backfill work can't starve post-scrape work.
/// </summary>
public sealed class SharedDopPool : IDisposable
{
    private readonly AdaptiveConcurrencyLimiter _inner;
    private readonly SemaphoreSlim _lowPriorityGate;
    private readonly bool _ownsInner;

    /// <summary>
    /// Create a pool with the given DOP configuration and low-priority cap.
    /// </summary>
    /// <param name="initialDop">Initial degree of parallelism.</param>
    /// <param name="minDop">Minimum DOP the AIMD can reduce to.</param>
    /// <param name="maxDop">Maximum DOP the AIMD can increase to.</param>
    /// <param name="lowPriorityPercent">Percentage of <paramref name="maxDop"/> available to low-priority callers (0–100).</param>
    /// <param name="log">Logger for AIMD adjustments.</param>
    /// <param name="maxRequestsPerSecond">Hard cap on requests per second (0 = unlimited).</param>
    public SharedDopPool(int initialDop, int minDop, int maxDop, int lowPriorityPercent, ILogger log,
        int maxRequestsPerSecond = 0)
    {
        _inner = new AdaptiveConcurrencyLimiter(initialDop, minDop, maxDop, log, maxRequestsPerSecond);
        int lowPrioritySlots = Math.Max(1, maxDop * Math.Clamp(lowPriorityPercent, 1, 100) / 100);
        _lowPriorityGate = new SemaphoreSlim(lowPrioritySlots, lowPrioritySlots);
        _ownsInner = true;
    }

    /// <summary>
    /// Create a pool wrapping an existing limiter (for testing).
    /// </summary>
    internal SharedDopPool(AdaptiveConcurrencyLimiter inner, int lowPrioritySlots)
    {
        _inner = inner;
        _lowPriorityGate = new SemaphoreSlim(lowPrioritySlots, lowPrioritySlots);
        _ownsInner = false;
    }

    /// <summary>Current effective DOP from the inner AIMD limiter.</summary>
    public int CurrentDop => _inner.CurrentDop;

    /// <summary>The inner limiter, for registering with <see cref="ScrapeProgressTracker"/>.</summary>
    public AdaptiveConcurrencyLimiter Limiter => _inner;

    // ─── High-priority access ────────────────────────────────

    /// <summary>Acquire a slot at high priority (direct access to full DOP).</summary>
    public Task AcquireHighAsync(CancellationToken ct) => _inner.WaitAsync(ct);

    /// <summary>Release a high-priority slot.</summary>
    public void ReleaseHigh() => _inner.Release();

    // ─── Low-priority access ─────────────────────────────────

    /// <summary>
    /// Acquire a slot at low priority. Waits for the low-priority gate first
    /// (capping concurrent low-priority holders), then acquires the inner limiter.
    /// </summary>
    public async Task AcquireLowAsync(CancellationToken ct)
    {
        await _lowPriorityGate.WaitAsync(ct);
        try
        {
            await _inner.WaitAsync(ct);
        }
        catch
        {
            _lowPriorityGate.Release();
            throw;
        }
    }

    /// <summary>Release a low-priority slot (inner limiter + gate).</summary>
    public void ReleaseLow()
    {
        _inner.Release();
        _lowPriorityGate.Release();
    }

    // ─── AIMD feedback ───────────────────────────────────────

    /// <summary>Report a successful request to the AIMD algorithm.</summary>
    public void ReportSuccess() => _inner.ReportSuccess();

    /// <summary>Report a failed/retried request to the AIMD algorithm.</summary>
    public void ReportFailure() => _inner.ReportFailure();

    public void Dispose()
    {
        _lowPriorityGate.Dispose();
        if (_ownsInner)
            _inner.Dispose();
    }
}
