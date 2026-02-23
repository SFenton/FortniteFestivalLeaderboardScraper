using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PercentileService;

/// <summary>
/// Background worker that keeps the Epic token alive by refreshing it on a schedule.
/// </summary>
public sealed class TokenRefreshWorker : BackgroundService
{
    private readonly EpicTokenManager _tokenManager;
    private readonly ILogger<TokenRefreshWorker> _log;
    private readonly PercentileOptions _options;

    public TokenRefreshWorker(
        EpicTokenManager tokenManager,
        ILogger<TokenRefreshWorker> log,
        IOptions<PercentileOptions> options)
    {
        _tokenManager = tokenManager;
        _log = log;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Initial auth (device_code if first time, else refresh from disk)
        _log.LogInformation("TokenRefreshWorker starting. Ensuring initial authentication...");
        try
        {
            await _tokenManager.EnsureAuthenticatedAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Initial authentication failed. Service cannot operate.");
            throw;
        }

        _log.LogInformation("Token refresh loop started. Interval: {Interval}", _options.TokenRefreshInterval);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.TokenRefreshInterval, ct);
                _log.LogDebug("Refreshing Epic token...");
                await _tokenManager.RefreshAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Token refresh failed. Will retry in {Interval}.",
                    _options.TokenRefreshInterval);
            }
        }
    }
}
