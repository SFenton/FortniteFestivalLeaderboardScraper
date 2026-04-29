namespace FSTService.Scraping;

internal sealed record SongMachineLookupResult<T>(bool Succeeded, T? Value) where T : class;

/// <summary>
/// Shared DOP-slot and CDN-resilience wrapper for song-machine API calls.
/// Solo and band machines should use this instead of duplicating acquire/release/retry plumbing.
/// </summary>
internal sealed class SongMachineApiLookupRunner
{
    private readonly ResilientHttpExecutor? _executor;
    private readonly ScrapeProgressTracker _progress;

    public SongMachineApiLookupRunner(ResilientHttpExecutor? executor, ScrapeProgressTracker progress)
    {
        _executor = executor;
        _progress = progress;
    }

    public async Task<SongMachineLookupResult<T>> TryRunAsync<T>(
        SharedDopPool pool,
        bool isHighPriority,
        CancellationToken ct,
        Func<Task<T>> work,
        Action<Exception> onFailure)
        where T : class
    {
        LowPriorityToken lowPriorityToken = default;

        Func<Task> acquireSlot = async () =>
        {
            if (isHighPriority) await pool.AcquireHighAsync(ct);
            else lowPriorityToken = await pool.AcquireLowAsync(ct);
        };

        Action releaseSlot = () =>
        {
            if (isHighPriority) pool.ReleaseHigh();
            else pool.ReleaseLow(lowPriorityToken);
        };

        try
        {
            var value = _executor is not null
                ? await _executor.WithCdnResilienceAsync(work, ct, acquireSlot, releaseSlot)
                : await FallbackAcquireAndRunAsync(acquireSlot, work, releaseSlot);
            return new SongMachineLookupResult<T>(true, value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            onFailure(ex);
            _progress.ReportPhaseRetry();
            return new SongMachineLookupResult<T>(false, null);
        }
    }

    private static async Task<T> FallbackAcquireAndRunAsync<T>(
        Func<Task> acquireSlot,
        Func<Task<T>> work,
        Action releaseSlot)
    {
        await acquireSlot();
        try
        {
            return await work();
        }
        finally
        {
            releaseSlot();
        }
    }
}