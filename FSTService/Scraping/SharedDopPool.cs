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
    private int _highPriorityActive;

    /// <summary>
    /// Counter for registered high-priority phases (e.g. solo scrape).
    /// While > 0, <see cref="AcquireLowAsync"/> enforces the low-priority gate
    /// even if no individual high-priority slots are currently held.
    /// This prevents band from consuming all DOP when solo is between
    /// slot acquire/release cycles (e.g. between songs).
    /// </summary>
    private int _highPriorityPhaseActive;

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
        int maxRequestsPerSecond = 0, int initialSsthresh = 0)
    {
        _inner = new AdaptiveConcurrencyLimiter(initialDop, minDop, maxDop, log, maxRequestsPerSecond, initialSsthresh);
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
    public async Task AcquireHighAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _highPriorityActive);
        try
        {
            await _inner.WaitAsync(ct);
        }
        catch
        {
            Interlocked.Decrement(ref _highPriorityActive);
            throw;
        }
    }

    /// <summary>Release a high-priority slot.</summary>
    public void ReleaseHigh()
    {
        _inner.Release();
        Interlocked.Decrement(ref _highPriorityActive);
    }

    // ─── Low-priority access ─────────────────────────────────

    /// <summary>
    /// Acquire a slot at low priority. When a high-priority phase is registered
    /// or individual high-priority slots are held, waits for the low-priority gate
    /// first (capping concurrent low-priority holders).
    /// When no high-priority work is active, bypasses the gate to use the full DOP budget.
    /// </summary>
    public async Task<LowPriorityToken> AcquireLowAsync(CancellationToken ct)
    {
        bool gateAcquired = false;

        // Enforce the low-priority cap when any high-priority work is present:
        // either a registered phase (solo scrape) or individual slot holders (post-scrape).
        if (Volatile.Read(ref _highPriorityPhaseActive) > 0 ||
            Volatile.Read(ref _highPriorityActive) > 0)
        {
            await _lowPriorityGate.WaitAsync(ct);
            gateAcquired = true;
        }

        try
        {
            await _inner.WaitAsync(ct);
        }
        catch
        {
            if (gateAcquired) _lowPriorityGate.Release();
            throw;
        }

        return new LowPriorityToken { GateAcquired = gateAcquired };
    }

    /// <summary>Release a low-priority slot (inner limiter + gate if acquired).</summary>
    public void ReleaseLow(LowPriorityToken token)
    {
        _inner.Release();
        if (token.GateAcquired) _lowPriorityGate.Release();
    }

    // ─── High-priority phase registration ───────────────────

    /// <summary>
    /// Register that a high-priority phase (e.g. solo scrape) is active.
    /// While registered, <see cref="AcquireLowAsync"/> enforces the low-priority
    /// gate regardless of whether individual high-priority slots are held.
    /// Call <see cref="EndHighPriorityPhase"/> when the phase completes.
    /// </summary>
    public void BeginHighPriorityPhase() => Interlocked.Increment(ref _highPriorityPhaseActive);

    /// <summary>
    /// Unregister a high-priority phase. When no phases or individual slots remain,
    /// low-priority callers bypass the gate and can use the full DOP budget.
    /// </summary>
    public void EndHighPriorityPhase() => Interlocked.Decrement(ref _highPriorityPhaseActive);

    // ─── AIMD feedback ───────────────────────────────────────

    /// <summary>Report a successful request to the AIMD algorithm.</summary>
    public void ReportSuccess() => _inner.ReportSuccess();

    /// <summary>Report a failed/retried request to the AIMD algorithm.</summary>
    public void ReportFailure() => _inner.ReportFailure();

    /// <summary>
    /// Reset DOP to the initial configured value. Call between scrape passes
    /// so a CDN slash from a previous pass doesn't cripple the next one.
    /// </summary>
    public void ResetDop() => _inner.ResetDop();

    public void Dispose()
    {
        _lowPriorityGate.Dispose();
        if (_ownsInner)
            _inner.Dispose();
    }
}

/// <summary>
/// Token returned by <see cref="SharedDopPool.AcquireLowAsync"/> indicating whether
/// the low-priority gate was acquired. Must be passed to <see cref="SharedDopPool.ReleaseLow"/>
/// to correctly release only the resources that were acquired.
/// </summary>
public readonly struct LowPriorityToken
{
    internal bool GateAcquired { get; init; }
}
