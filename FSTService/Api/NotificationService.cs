using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FSTService.Scraping;

namespace FSTService.Api;

/// <summary>
/// Manages WebSocket connections for pushing real-time notifications
/// to connected mobile clients (e.g. when backfill completes).
///
/// Clients connect via <c>GET /api/ws?token={jwt}</c> and receive
/// JSON messages like <c>{"type":"personal_db_ready"}</c>.
/// </summary>
public sealed class NotificationService
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, WebSocket>> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<NotificationService> _log;
    private IShopProvider? _shopProvider;

    public NotificationService(ILogger<NotificationService> log)
    {
        _log = log;
    }

    /// <summary>
    /// Set the shop provider for sending snapshots on connect.
    /// Called during startup to break the circular dependency.
    /// </summary>
    public void SetShopProvider(IShopProvider provider) => _shopProvider = provider;

    /// <summary>
    /// Register a WebSocket connection for the given account+device pair.
    /// </summary>
    public void AddConnection(string accountId, string deviceId, WebSocket ws)
    {
        var deviceMap = _connections.GetOrAdd(accountId, _ => new ConcurrentDictionary<string, WebSocket>(StringComparer.OrdinalIgnoreCase));
        deviceMap[deviceId] = ws;
        _log.LogInformation("WebSocket connected: account={AccountId}, device={DeviceId}. Total connections for account: {Count}",
            accountId, deviceId, deviceMap.Count);
    }

    /// <summary>
    /// Remove a WebSocket connection for the given account+device pair.
    /// </summary>
    public void RemoveConnection(string accountId, string deviceId)
    {
        if (_connections.TryGetValue(accountId, out var deviceMap))
        {
            deviceMap.TryRemove(deviceId, out _);
            if (deviceMap.IsEmpty)
                _connections.TryRemove(accountId, out _);
        }
        _log.LogInformation("WebSocket disconnected: account={AccountId}, device={DeviceId}", accountId, deviceId);
    }

    /// <summary>
    /// Notify all connected devices for a given account.
    /// </summary>
    public async Task NotifyAccountAsync(string accountId, object message)
    {
        if (!_connections.TryGetValue(accountId, out var deviceMap))
            return;

        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        var segment = new ArraySegment<byte>(bytes);
        var deadConnections = new List<string>();

        foreach (var (deviceId, ws) in deviceMap)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    await ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                    _log.LogDebug("Sent notification to {AccountId}/{DeviceId}: {Type}", accountId, deviceId, json);
                }
                else
                {
                    deadConnections.Add(deviceId);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to send notification to {AccountId}/{DeviceId}", accountId, deviceId);
                deadConnections.Add(deviceId);
            }
        }

        // Clean up dead connections
        foreach (var deviceId in deadConnections)
        {
            RemoveConnection(accountId, deviceId);
        }
    }

    /// <summary>
    /// Broadcast a message to ALL connected WebSocket clients (every account, every device).
    /// Used for global events like shop rotation.
    /// </summary>
    public async Task BroadcastAllAsync(object message)
    {
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        var segment = new ArraySegment<byte>(bytes);

        foreach (var (accountId, deviceMap) in _connections)
        {
            var deadConnections = new List<string>();
            foreach (var (deviceId, ws) in deviceMap)
            {
                try
                {
                    if (ws.State == WebSocketState.Open)
                        await ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                    else
                        deadConnections.Add(deviceId);
                }
                catch
                {
                    deadConnections.Add(deviceId);
                }
            }
            foreach (var deviceId in deadConnections)
                RemoveConnection(accountId, deviceId);
        }

        _log.LogInformation("Broadcast to all clients: {Message}", json);
    }

    /// <summary>
    /// Broadcast that the item shop has changed. Sends added/removed songId deltas.
    /// </summary>
    public Task NotifyShopChangedAsync(
        IReadOnlyCollection<string> added,
        IReadOnlyCollection<string> removed,
        int total,
        IReadOnlyCollection<string> leavingTomorrow)
    {
        return BroadcastAllAsync(new { type = "shop_changed", added, removed, total, leavingTomorrow });
    }

    /// <summary>
    /// Send the current shop snapshot to a single WebSocket (used on reconnect).
    /// </summary>
    public async Task SendShopSnapshotAsync(WebSocket ws, IReadOnlyCollection<string> songIds, IReadOnlyCollection<string> leavingTomorrow)
    {
        var json = JsonSerializer.Serialize(new { type = "shop_snapshot", songIds, total = songIds.Count, leavingTomorrow });
        var bytes = Encoding.UTF8.GetBytes(json);
        if (ws.State == WebSocketState.Open)
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    /// <summary>
    /// Notify an account that their personal DB has been rebuilt and is ready for download.
    /// </summary>
    public Task NotifyPersonalDbReadyAsync(string accountId)
    {
        return NotifyAccountAsync(accountId, new { type = "personal_db_ready" });
    }

    /// <summary>
    /// Notify an account that backfill has completed.
    /// </summary>
    public Task NotifyBackfillCompleteAsync(string accountId)
    {
        return NotifyAccountAsync(accountId, new { type = "backfill_complete" });
    }

    /// <summary>
    /// Notify an account that history reconstruction has completed.
    /// </summary>
    public Task NotifyHistoryReconCompleteAsync(string accountId)
    {
        return NotifyAccountAsync(accountId, new { type = "history_recon_complete" });
    }

    /// <summary>
    /// Notify an account that rivals computation has completed.
    /// </summary>
    public Task NotifyRivalsCompleteAsync(string accountId)
    {
        return NotifyAccountAsync(accountId, new { type = "rivals_complete" });
    }

    /// <summary>
    /// Process a WebSocket connection — keep alive until closed by client or server shutdown.
    /// </summary>
    public async Task HandleConnectionAsync(string accountId, string deviceId, WebSocket ws, CancellationToken ct)
    {
        AddConnection(accountId, deviceId, ws);

        // Send current shop snapshot so the client is immediately up-to-date
        if (_shopProvider is not null)
        {
            try
            {
                var shopIds = _shopProvider.InShopSongIds;
                var leavingIds = _shopProvider.LeavingTomorrowSongIds;
                await SendShopSnapshotAsync(ws, shopIds.ToArray(), leavingIds.ToArray());
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to send shop snapshot on connect for {AccountId}/{DeviceId}", accountId, deviceId);
            }
        }

        try
        {
            // Read loop — we don't expect messages from clients, but we need
            // to read to detect close frames. Buffer is small since we only
            // care about control frames.
            var buffer = new byte[256];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                try
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Goodbye", CancellationToken.None);
                        break;
                    }
                }
                catch (WebSocketException)
                {
                    break; // Connection lost
                }
                catch (OperationCanceledException)
                {
                    break; // Server shutting down
                }
            }
        }
        finally
        {
            RemoveConnection(accountId, deviceId);
        }
    }
}
