using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FortniteFestival.Core.Services;
using FSTService.Persistence;
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
    private const int MaxControlMessageBytes = 16 * 1024;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, PublicationWebSocketConnection>> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<NotificationService> _log;
    private IShopProvider? _shopProvider;
    private FestivalService? _festivalService;
    private UserSyncProgressTracker? _syncTracker;
    private IMetaDatabase? _metaDb;

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
    /// Set the metadata store for DB-backed sync state on WebSocket subscribe.
    /// Called during startup to break the circular dependency.
    /// </summary>
    public void SetMetaDatabase(IMetaDatabase metaDb) => _metaDb = metaDb;

    /// <summary>
    /// Register a WebSocket connection for the given account+device pair.
    /// </summary>
    public void AddConnection(string accountId, string deviceId, WebSocket ws) =>
        AddConnection(accountId, deviceId, ws, publicationId: null);

    public void AddConnection(
        string accountId,
        string deviceId,
        WebSocket ws,
        long? publicationId)
    {
        var deviceMap = _connections.GetOrAdd(
            accountId,
            _ => new ConcurrentDictionary<string, PublicationWebSocketConnection>(
                StringComparer.OrdinalIgnoreCase));
        var connection = new PublicationWebSocketConnection(ws, publicationId);
        var replacedExisting =
            deviceMap.TryGetValue(deviceId, out var existing)
            && !ReferenceEquals(existing.Socket, ws);
        deviceMap[deviceId] = connection;
        if (replacedExisting)
        {
            _log.LogInformation("WebSocket replaced: account={AccountId}, device={DeviceId}. Total connections for account: {Count}",
                accountId, deviceId, deviceMap.Count);
            return;
        }

        _log.LogInformation("WebSocket connected: account={AccountId}, device={DeviceId}. Total connections for account: {Count}",
            accountId, deviceId, deviceMap.Count);
    }

    /// <summary>
    /// Remove a WebSocket connection for the given account+device pair.
    /// </summary>
    public void RemoveConnection(string accountId, string deviceId, WebSocket? expectedSocket = null)
    {
        var removed = false;
        if (_connections.TryGetValue(accountId, out var deviceMap))
        {
            if (expectedSocket is null)
            {
                removed = deviceMap.TryRemove(deviceId, out _);
            }
            else if (deviceMap.TryGetValue(deviceId, out var current)
                && ReferenceEquals(current.Socket, expectedSocket))
            {
                removed = deviceMap.TryRemove(deviceId, out _);
            }

            if (removed && deviceMap.IsEmpty)
            {
                _connections.TryRemove(accountId, out _);
            }
        }

        if (removed)
        {
            _log.LogInformation("WebSocket disconnected: account={AccountId}, device={DeviceId}", accountId, deviceId);
        }
        else if (expectedSocket is not null)
        {
            _log.LogDebug("Skipped stale WebSocket disconnect: account={AccountId}, device={DeviceId}", accountId, deviceId);
        }
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
        var deadConnections = new List<(string DeviceId, WebSocket Socket)>();
        var currentPublicationId = GetCurrentPublicationId();

        foreach (var (deviceId, connection) in deviceMap)
        {
            var ws = connection.Socket;
            try
            {
                if (!await EnsureCurrentPublicationAsync(
                        ws,
                        connection.PublicationId,
                        currentPublicationId))
                {
                    deadConnections.Add((deviceId, ws));
                    continue;
                }

                if (ws.State == WebSocketState.Open)
                {
                    await ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                    _log.LogDebug("Sent notification to {AccountId}/{DeviceId}: {Type}", accountId, deviceId, json);
                }
                else
                {
                    deadConnections.Add((deviceId, ws));
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to send notification to {AccountId}/{DeviceId}", accountId, deviceId);
                deadConnections.Add((deviceId, ws));
            }
        }

        // Clean up dead connections
        foreach (var (deviceId, deadSocket) in deadConnections)
        {
            RemoveConnection(accountId, deviceId, deadSocket);
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
        var currentPublicationId = GetCurrentPublicationId();

        foreach (var (accountId, deviceMap) in _connections)
        {
            var deadConnections = new List<(string DeviceId, WebSocket Socket)>();
            foreach (var (deviceId, connection) in deviceMap)
            {
                var ws = connection.Socket;
                try
                {
                    if (!await EnsureCurrentPublicationAsync(
                            ws,
                            connection.PublicationId,
                            currentPublicationId))
                    {
                        deadConnections.Add((deviceId, ws));
                        continue;
                    }

                    if (ws.State == WebSocketState.Open)
                        await ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                    else
                        deadConnections.Add((deviceId, ws));
                }
                catch
                {
                    deadConnections.Add((deviceId, ws));
                }
            }

            foreach (var (deviceId, deadSocket) in deadConnections)
                RemoveConnection(accountId, deviceId, deadSocket);
        }

        _log.LogInformation("Broadcast to all clients: {Message}", json);
    }

    public async Task NotifyPublicationChangedAsync(long publicationId)
    {
        foreach (var (accountId, deviceMap) in _connections)
        {
            var deadConnections = new List<(string DeviceId, WebSocket Socket)>();
            foreach (var (deviceId, connection) in deviceMap)
            {
                try
                {
                    if (await EnsureCurrentPublicationAsync(
                            connection.Socket,
                            connection.PublicationId,
                            publicationId))
                    {
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(
                        ex,
                        "Failed to rotate WebSocket {AccountId}/{DeviceId} to publication {PublicationId}.",
                        accountId,
                        deviceId,
                        publicationId);
                }

                deadConnections.Add((deviceId, connection.Socket));
            }

            foreach (var (deviceId, socket) in deadConnections)
                RemoveConnection(accountId, deviceId, socket);
        }
    }

    /// <summary>
    /// Broadcast that the item shop has changed. Sends enriched added song objects and removed songId strings.
    /// </summary>
    public Task NotifyShopChangedAsync(
        IReadOnlyCollection<object> addedEnriched,
        IReadOnlyCollection<string> removed,
        int total,
        IReadOnlyCollection<string> leavingTomorrow,
        IReadOnlyCollection<string> newSongs)
    {
        return BroadcastAllAsync(new { type = "shop_changed", added = addedEnriched, removed, total, leavingTomorrow, newSongs });
    }

    /// <summary>
    /// Broadcast that the song catalog has changed.
    /// Clients use this to invalidate /api/songs-driven views.
    /// </summary>
    public Task NotifySongsChangedAsync(int total, int added)
    {
        return BroadcastAllAsync(new
        {
            type = "songs_changed",
            total,
            added,
            at = DateTime.UtcNow.ToString("o"),
        });
    }

    /// <summary>
    /// Broadcast that score-backed read models were refreshed at the end of a scrape cycle.
    /// Clients use this to invalidate leaderboard/player/ranking queries.
    /// </summary>
    public Task NotifyScoresChangedAsync(long? scrapeId)
    {
        return BroadcastAllAsync(new
        {
            type = "scores_changed",
            scrapeId,
            at = DateTime.UtcNow.ToString("o"),
        });
    }

    public Task NotifyNotificationFeedChangedAsync()
    {
        return BroadcastAllAsync(new
        {
            type = "notification_feed_changed",
            at = DateTime.UtcNow.ToString("o"),
        });
    }

    /// <summary>
    /// Send the current shop snapshot to a single WebSocket (used on reconnect).
    /// Sends enriched song objects so the client can render the shop page without /api/songs.
    /// </summary>
    public async Task SendShopSnapshotAsync(WebSocket ws, IReadOnlyCollection<object> enrichedSongs, IReadOnlyCollection<string> leavingTomorrow, IReadOnlyCollection<string> newSongs)
    {
        var json = JsonSerializer.Serialize(new { type = "shop_snapshot", songs = enrichedSongs, total = enrichedSongs.Count, leavingTomorrow, newSongs });
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
    public Task HandleConnectionAsync(
        string accountId,
        string deviceId,
        WebSocket ws,
        CancellationToken ct) =>
        HandleConnectionAsync(
            accountId,
            deviceId,
            ws,
            publicationId: null,
            publicationService: null,
            ct);

    public async Task HandleConnectionAsync(
        string accountId,
        string deviceId,
        WebSocket ws,
        long? publicationId,
        PublicationReadContextService? publicationService,
        CancellationToken ct)
    {
        var currentKey = accountId;
        PublicationReadLease? registrationLease = null;
        try
        {
            if (publicationId.HasValue
                && publicationService?.PinningEnabled == true)
            {
                registrationLease =
                    await publicationService.AcquireAsync(ct);
                if (!await EnsureCurrentPublicationAsync(
                        ws,
                        publicationId,
                        registrationLease.Pointers.CurrentPublicationId))
                {
                    return;
                }
            }

            AddConnection(currentKey, deviceId, ws, publicationId);

            // Send current shop snapshot so the client is immediately up-to-date.
            if (_shopProvider is not null && _festivalService is not null)
            {
                try
                {
                    var shopIds = _shopProvider.InShopSongIds;
                    var leavingIds = _shopProvider.LeavingTomorrowSongIds;
                    var newIds = _shopProvider.NewSongIds;
                    var enrichedSongs = ShopCacheService.BuildEnrichedSongList(
                        shopIds, leavingIds, newIds, _festivalService);
                    await SendShopSnapshotAsync(
                        ws,
                        enrichedSongs,
                        leavingIds.ToArray(),
                        newIds.ToArray());
                }
                catch (Exception ex)
                {
                    _log.LogWarning(
                        ex,
                        "Failed to send shop snapshot on connect for {AccountId}/{DeviceId}",
                        accountId,
                        deviceId);
                }
            }
        }
        finally
        {
            if (registrationLease is not null)
                await registrationLease.DisposeAsync();
        }

        try
        {
            // Read loop — process control messages (subscribe/unsubscribe) and detect close frames.
            var buffer = new byte[1024];
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
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var (json, closeRequested) = await ReadTextMessageAsync(ws, result, buffer, deviceId, ct);
                        if (closeRequested)
                        {
                            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Goodbye", CancellationToken.None);
                            break;
                        }

                        if (string.IsNullOrEmpty(json))
                        {
                            continue;
                        }

                        try
                        {
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
                                AddConnection(
                                    currentKey,
                                    deviceId,
                                    ws,
                                    publicationId);
                                _log.LogDebug("WebSocket {DeviceId} subscribed to account {AccountId}.", deviceId, currentKey);

                                // Send current sync state immediately so late subscribers
                                // don't miss fast backfills that complete before the WS connects.
                                if (BuildInitialSyncStatePayload(requestedAccountId) is { } payload)
                                {
                                    try
                                    {
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
                                    AddConnection(
                                        currentKey,
                                        deviceId,
                                        ws,
                                        publicationId);
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
            RemoveConnection(currentKey, deviceId, ws);
        }
    }

    private object? BuildInitialSyncStatePayload(string accountId)
    {
        var liveProgress = _syncTracker?.GetProgress(accountId);
        if (liveProgress is not null && liveProgress.Phase != SyncProgressPhase.Queued)
        {
            return _syncTracker!.BuildPayloadForAccount(accountId, liveProgress);
        }

        var durablePayload = BuildDurableSyncStatePayload(accountId);
        if (durablePayload is not null)
            return durablePayload;

        return liveProgress is null ? null : _syncTracker!.BuildPayloadForAccount(accountId, liveProgress);
    }

    private object? BuildDurableSyncStatePayload(string accountId)
    {
        if (_metaDb is null) return null;

        var backfill = _metaDb.GetBackfillStatus(accountId);
        if (backfill is not null)
        {
            var backfillDisplay = _metaDb.GetBackfillSongProgress(accountId, backfill.SongsChecked, backfill.TotalSongsToCheck);
            var backfillPayload = backfill.Status switch
            {
                "deferred" => BuildSyncProgressPayload(accountId, "queued", 0, backfill.TotalSongsToCheck, backfill.EntriesFound, displayItemsCompleted: 0, displayTotalItems: backfillDisplay?.TotalSongs, pendingRankUpdate: backfill.RankingsPending),
                "pending" or "in_progress" => BuildSyncProgressPayload(accountId, "backfill", backfill.SongsChecked, backfill.TotalSongsToCheck, backfill.EntriesFound, displayItemsCompleted: backfillDisplay?.SongsChecked, displayTotalItems: backfillDisplay?.TotalSongs, pendingRankUpdate: backfill.RankingsPending),
                "error" => BuildSyncProgressPayload(accountId, "error", backfill.SongsChecked, backfill.TotalSongsToCheck, backfill.EntriesFound, displayItemsCompleted: backfillDisplay?.SongsChecked, displayTotalItems: backfillDisplay?.TotalSongs, pendingRankUpdate: backfill.RankingsPending),
                _ => null,
            };
            if (backfillPayload is not null)
                return backfillPayload;
        }

        var history = _metaDb.GetHistoryReconStatus(accountId);
        if (history?.Status is "in_progress")
            return BuildSyncProgressPayload(accountId, "history", history.SongsProcessed, history.TotalSongsToProcess, history.HistoryEntriesFound, seasonsQueried: history.SeasonsQueried);
        if (history?.Status is "error")
            return BuildSyncProgressPayload(accountId, "error", history.SongsProcessed, history.TotalSongsToProcess, history.HistoryEntriesFound, seasonsQueried: history.SeasonsQueried);

        var rivals = _metaDb.GetRivalsStatus(accountId);
        if (rivals?.Status is "pending" or "in_progress")
            return BuildSyncProgressPayload(accountId, "rivals", rivals.CombosComputed, rivals.TotalCombosToCompute, 0, rivalsFound: rivals.RivalsFound);
        if (rivals?.Status is "error")
            return BuildSyncProgressPayload(accountId, "error", rivals.CombosComputed, rivals.TotalCombosToCompute, 0, rivalsFound: rivals.RivalsFound);

        if (backfill?.Status is "complete")
        {
            var backfillDisplay = _metaDb.GetBackfillSongProgress(accountId, backfill.SongsChecked, backfill.TotalSongsToCheck);
            return BuildSyncProgressPayload(accountId, "complete", backfill.TotalSongsToCheck, backfill.TotalSongsToCheck, backfill.EntriesFound, displayItemsCompleted: backfillDisplay?.TotalSongs, displayTotalItems: backfillDisplay?.TotalSongs, pendingRankUpdate: backfill.RankingsPending);
        }
        if (history?.Status is "complete")
            return BuildSyncProgressPayload(accountId, "complete", history.TotalSongsToProcess, history.TotalSongsToProcess, history.HistoryEntriesFound, seasonsQueried: history.SeasonsQueried, pendingRankUpdate: backfill?.RankingsPending);

        return null;
    }

    private static object BuildSyncProgressPayload(
        string accountId,
        string phase,
        int itemsCompleted,
        int totalItems,
        int entriesFound,
        int? displayItemsCompleted = null,
        int? displayTotalItems = null,
        int seasonsQueried = 0,
        int rivalsFound = 0,
        bool? pendingRankUpdate = null)
    {
        return new
        {
            type = "sync_progress",
            accountId,
            phase,
            itemsCompleted,
            totalItems,
            displayItemsCompleted,
            displayTotalItems,
            entriesFound,
            currentSongName = (string?)null,
            seasonsQueried,
            rivalsFound,
            elapsedSeconds = 0,
            isThrottled = false,
            throttleStatusKey = (string?)null,
            probeStatusKey = (string?)null,
            nextRetrySeconds = (double?)null,
            probeAttempt = (int?)null,
            pendingRankUpdate,
            estimatedRankUpdateMinutes = (int?)null,
        };
    }

    private async Task<(string? MessageText, bool CloseRequested)> ReadTextMessageAsync(
        WebSocket ws,
        WebSocketReceiveResult initialResult,
        byte[] buffer,
        string deviceId,
        CancellationToken ct)
    {
        using var messageBuffer = new MemoryStream();
        var result = initialResult;

        while (true)
        {
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (null, true);
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                return (null, false);
            }

            if (result.Count > 0)
            {
                if (messageBuffer.Length + result.Count > MaxControlMessageBytes)
                {
                    await DrainMessageAsync(ws, result, buffer, ct);
                    _log.LogDebug("Ignoring oversized WebSocket control message for {DeviceId}.", deviceId);
                    return (null, false);
                }

                messageBuffer.Write(buffer, 0, result.Count);
            }

            if (result.EndOfMessage)
            {
                break;
            }

            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
        }

        if (messageBuffer.Length == 0)
        {
            return (string.Empty, false);
        }

        return (Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, checked((int)messageBuffer.Length)), false);
    }

    private static async Task DrainMessageAsync(
        WebSocket ws,
        WebSocketReceiveResult initialResult,
        byte[] buffer,
        CancellationToken ct)
    {
        var result = initialResult;
        while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close)
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
        }
    }

    private long? GetCurrentPublicationId() =>
        _metaDb?.GetPublicationPointerState().CurrentPublicationId;

    private static async Task<bool> EnsureCurrentPublicationAsync(
        WebSocket ws,
        long? connectionPublicationId,
        long? currentPublicationId)
    {
        if (!connectionPublicationId.HasValue
            || !currentPublicationId.HasValue
            || connectionPublicationId.Value == currentPublicationId.Value)
        {
            return true;
        }

        if (ws.State == WebSocketState.Open)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "publication_changed",
                publicationId = currentPublicationId.Value,
            });
            await ws.SendAsync(
                payload,
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);
            await ws.CloseOutputAsync(
                WebSocketCloseStatus.PolicyViolation,
                "Publication changed",
                CancellationToken.None);
        }

        return false;
    }

    private sealed record PublicationWebSocketConnection(
        WebSocket Socket,
        long? PublicationId);
}
