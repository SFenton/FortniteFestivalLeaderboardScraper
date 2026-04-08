using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FortniteFestival.Core.Services;
using FSTService.Scraping;

namespace FSTService.Api;

/// <summary>
/// Manages WebSocket connections for pushing real-time notifications
/// to connected mobile clients (e.g. when backfill completes).
///
/// Clients connect via <c>GET /api/ws?token={jwt}</c> and receive
/// JSON messages like <c>{"type":"backfill_complete"}</c>.
/// </summary>
public sealed class NotificationService
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, WebSocket>> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<NotificationService> _log;
    private IShopProvider? _shopProvider;
    private FestivalService? _festivalService;
    private UserSyncProgressTracker? _syncTracker;

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
    /// Set the festival service for enriching shop snapshots with song metadata.
    /// </summary>
    public void SetFestivalService(FestivalService service) => _festivalService = service;

    /// <summary>
    /// Set the sync tracker for pushing current state on subscribe.
    /// Called during startup to break the circular dependency.
    /// </summary>
    public void SetSyncTracker(UserSyncProgressTracker tracker) => _syncTracker = tracker;

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
    /// Broadcast that the item shop has changed. Sends enriched added song objects and removed songId strings.
    /// </summary>
    public Task NotifyShopChangedAsync(
        IReadOnlyCollection<object> addedEnriched,
        IReadOnlyCollection<string> removed,
        int total,
        IReadOnlyCollection<string> leavingTomorrow)
    {
        return BroadcastAllAsync(new { type = "shop_changed", added = addedEnriched, removed, total, leavingTomorrow });
    }

    /// <summary>
    /// Send the current shop snapshot to a single WebSocket (used on reconnect).
    /// Sends enriched song objects so the client can render the shop page without /api/songs.
    /// </summary>
    public async Task SendShopSnapshotAsync(WebSocket ws, IReadOnlyCollection<object> enrichedSongs, IReadOnlyCollection<string> leavingTomorrow)
    {
        var json = JsonSerializer.Serialize(new { type = "shop_snapshot", songs = enrichedSongs, total = enrichedSongs.Count, leavingTomorrow });
        var bytes = Encoding.UTF8.GetBytes(json);
        if (ws.State == WebSocketState.Open)
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
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
    /// Push a sync progress update to a specific account's connected devices.
    /// Called by <see cref="Scraping.UserSyncProgressTracker"/> at a throttled rate.
    /// </summary>
    public Task NotifySyncProgressAsync(string accountId, object progressPayload)
    {
        return NotifyAccountAsync(accountId, progressPayload);
    }

    /// <summary>
    /// Process a WebSocket connection — keep alive until closed by client or server shutdown.
    /// </summary>
    public async Task HandleConnectionAsync(string accountId, string deviceId, WebSocket ws, CancellationToken ct)
    {
        var currentKey = accountId;
        AddConnection(currentKey, deviceId, ws);

        // Send current shop snapshot so the client is immediately up-to-date
        if (_shopProvider is not null && _festivalService is not null)
        {
            try
            {
                var shopIds = _shopProvider.InShopSongIds;
                var leavingIds = _shopProvider.LeavingTomorrowSongIds;
                var enrichedSongs = ShopCacheService.BuildEnrichedSongList(
                    shopIds, leavingIds, _festivalService);
                await SendShopSnapshotAsync(ws, enrichedSongs, leavingIds.ToArray());
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to send shop snapshot on connect for {AccountId}/{DeviceId}", accountId, deviceId);
            }
        }

        try
        {
            // Read loop — process control messages (subscribe/unsubscribe) and detect close frames.
            var buffer = new byte[512];
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

                    // Process text frames for subscribe/unsubscribe control messages
                    if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                    {
                        try
                        {
                            var json = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                            using var doc = System.Text.Json.JsonDocument.Parse(json);
                            var action = doc.RootElement.TryGetProperty("action", out var actionProp)
                                ? actionProp.GetString() : null;

                            if (action == "subscribe_sync"
                                && doc.RootElement.TryGetProperty("accountId", out var aidProp)
                                && aidProp.GetString() is { Length: > 0 } requestedAccountId)
                            {
                                // Rebind: move this socket from current key to the requested accountId
                                RemoveConnection(currentKey, deviceId);
                                currentKey = requestedAccountId;
                                AddConnection(currentKey, deviceId, ws);
                                _log.LogDebug("WebSocket {DeviceId} subscribed to account {AccountId}.", deviceId, currentKey);

                                // Send current sync state immediately so late subscribers
                                // don't miss fast backfills that complete before the WS connects.
                                if (_syncTracker?.GetProgress(requestedAccountId) is { } currentProgress)
                                {
                                    try
                                    {
                                        var payload = _syncTracker.BuildPayloadForAccount(requestedAccountId, currentProgress);
                                        var payloadJson = JsonSerializer.Serialize(payload);
                                        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
                                        await ws.SendAsync(payloadBytes, WebSocketMessageType.Text, true, ct);
                                    }
                                    catch (Exception ex)
                                    {
                                        _log.LogDebug(ex, "Failed to send initial sync state to {DeviceId}.", deviceId);
                                    }
                                }
                            }
                            else if (action == "unsubscribe_sync")
                            {
                                // Move back to the original anonymous key
                                if (currentKey != accountId)
                                {
                                    RemoveConnection(currentKey, deviceId);
                                    currentKey = accountId;
                                    AddConnection(currentKey, deviceId, ws);
                                    _log.LogDebug("WebSocket {DeviceId} unsubscribed, reverted to {AccountId}.", deviceId, currentKey);
                                }
                            }
                        }
                        catch (System.Text.Json.JsonException)
                        {
                            // Malformed JSON — ignore silently
                        }
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
            RemoveConnection(currentKey, deviceId);
        }
    }
}
