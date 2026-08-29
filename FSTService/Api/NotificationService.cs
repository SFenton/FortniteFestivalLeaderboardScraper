using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FortniteFestival.Core.Services;
using FSTService.Persistence;
using FSTService.Scraping;
using Npgsql;

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
    private readonly object _connectionMutationGate = new();
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
        lock (_connectionMutationGate)
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
    }

    /// <summary>
    /// Remove a WebSocket connection for the given account+device pair.
    /// </summary>
    public void RemoveConnection(string accountId, string deviceId, WebSocket? expectedSocket = null)
    {
        lock (_connectionMutationGate)
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

    public async Task NotifyPublicationChangedAsync(
        long publicationId,
        bool forceRefresh = false)
    {
        List<(
            string AccountId,
            string DeviceId,
            PublicationWebSocketConnection Connection)> connections;
        lock (_connectionMutationGate)
        {
            connections =
                _connections
                    .SelectMany(static account =>
                        account.Value.Select(device => (
                            account.Key,
                            device.Key,
                            device.Value)))
                    .ToList();
        }

        var deadConnections =
            new List<(
                string AccountId,
                string DeviceId,
                WebSocket Socket)>();
        foreach (var (
                     accountId,
                     deviceId,
                     connection) in connections)
        {
            try
            {
                if (await EnsureCurrentPublicationAsync(
                        connection.Socket,
                        connection.PublicationId,
                        publicationId,
                        forceRefresh))
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

            deadConnections.Add((
                accountId,
                deviceId,
                connection.Socket));
        }

        foreach (var (
                     accountId,
                     deviceId,
                     socket) in deadConnections)
            RemoveConnection(accountId, deviceId, socket);
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
    /// Clients use this to refresh operational catalog-lag state. Canonical
    /// song data remains publication-bound.
    /// </summary>
    public Task NotifySongsChangedAsync(
        int total,
        int added,
        int removed,
        int changed,
        int? publishedTotal,
        int? awaitingPublication)
    {
        var message = new Dictionary<string, object?>
        {
            ["type"] = "songs_changed",
            ["total"] = total,
            ["added"] = added,
            ["removed"] = removed,
            ["changed"] = changed,
            ["at"] = DateTime.UtcNow.ToString("o"),
        };
        if (publishedTotal.HasValue)
            message["publishedTotal"] = publishedTotal.Value;
        if (awaitingPublication.HasValue)
        {
            message["awaitingPublication"] =
                awaitingPublication.Value;
        }
        return BroadcastAllAsync(message);
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
        var connectionRegistered = false;
        long? changedPublicationId = null;
        var publicationUnavailable = false;
        try
        {
            if (publicationService is not null
                && (publicationService.PinningConfigured
                    || publicationService
                        .PublishedScopeSourceReadinessRequired)
                && !publicationId.HasValue)
            {
                if (ws.State == WebSocketState.Open)
                {
                    await ws.CloseOutputAsync(
                        WebSocketCloseStatus.PolicyViolation,
                        "Publication unavailable",
                        CancellationToken.None);
                }
                return;
            }

            if (publicationId.HasValue
                && publicationService is not null)
            {
                try
                {
                    registrationLease =
                        publicationService.PinningConfigured
                            ? await publicationService
                                .AcquireAsync(ct)
                            : await publicationService
                                .AcquireWebSocketAdmissionAsync(ct);
                    var pointers =
                        registrationLease.Pointers;
                    if (!pointers.CurrentPublicationId.HasValue)
                    {
                        publicationUnavailable = true;
                    }
                    else if (pointers.CurrentPublicationId.Value !=
                             publicationId.Value)
                    {
                        changedPublicationId =
                            pointers.CurrentPublicationId.Value;
                    }
                    else
                    {
                        var sourceReadiness =
                            publicationService
                                .EvaluatePublishedScopeSourceReadiness(
                                    pointers,
                                    forceRefresh: true);
                        if (!pointers.PublishedScrapeId.HasValue
                            || !sourceReadiness.Ready
                            || publicationService.PinningConfigured
                            && !publicationService
                                .EvaluateReadiness(pointers)
                                .ReadyForPinning)
                        {
                            publicationUnavailable = true;
                        }
                        else
                        {
                            await registrationLease
                                .VerifyHeldAsync(ct);
                            AddConnection(
                                currentKey,
                                deviceId,
                                ws,
                                publicationId);
                            connectionRegistered = true;
                        }
                    }
                }
                catch (Exception ex) when (
                    ct.IsCancellationRequested
                    && (ex is NpgsqlException
                        or TimeoutException))
                {
                    throw new OperationCanceledException(
                        "WebSocket publication admission was cancelled.",
                        ex,
                        ct);
                }
                catch (Exception ex) when (
                    !ct.IsCancellationRequested
                    && (ex is NpgsqlException
                        or TimeoutException))
                {
                    publicationUnavailable = true;
                    _log.LogWarning(
                        ex,
                        "WebSocket publication admission could not acquire or validate the bounded shared publication lease for {AccountId}/{DeviceId}.",
                        accountId,
                        deviceId);
                }
                finally
                {
                    if (registrationLease is not null)
                    {
                        await registrationLease.DisposeAsync();
                        registrationLease = null;
                    }
                }

                if (!connectionRegistered)
                {
                    if (changedPublicationId.HasValue)
                    {
                        await EnsureCurrentPublicationAsync(
                            ws,
                            publicationId,
                            changedPublicationId);
                    }
                    else if (publicationUnavailable
                             && ws.State ==
                                WebSocketState.Open)
                    {
                        await ws.CloseOutputAsync(
                            WebSocketCloseStatus
                                .PolicyViolation,
                            "Publication unavailable",
                            CancellationToken.None);
                    }
                    return;
                }
            }
            else
            {
                AddConnection(
                    currentKey,
                    deviceId,
                    ws,
                    publicationId);
                connectionRegistered = true;
            }

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
                                var rebind =
                                    await TryRebindConnectionAsync(
                                        currentKey,
                                        requestedAccountId,
                                        deviceId,
                                        ws,
                                        publicationId,
                                        publicationService,
                                        ct);
                                if (!rebind.Rebound)
                                {
                                    if (rebind.ChangedPublicationId
                                            .HasValue)
                                    {
                                        await EnsureCurrentPublicationAsync(
                                            ws,
                                            publicationId,
                                            rebind
                                                .ChangedPublicationId);
                                    }
                                    else if (rebind
                                                 .PublicationUnavailable
                                             && ws.State ==
                                             WebSocketState.Open)
                                    {
                                        await ws.CloseOutputAsync(
                                            WebSocketCloseStatus
                                                .PolicyViolation,
                                            "Publication unavailable",
                                            CancellationToken.None);
                                    }
                                    break;
                                }

                                currentKey = requestedAccountId;
                                publicationId =
                                    rebind.PublicationId;
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
                                    var rebind =
                                        await TryRebindConnectionAsync(
                                            currentKey,
                                            accountId,
                                            deviceId,
                                            ws,
                                            publicationId,
                                            publicationService,
                                            ct);
                                    if (!rebind.Rebound)
                                    {
                                        if (rebind
                                                .ChangedPublicationId
                                                .HasValue)
                                        {
                                            await EnsureCurrentPublicationAsync(
                                                ws,
                                                publicationId,
                                                rebind
                                                    .ChangedPublicationId);
                                        }
                                        else if (rebind
                                                     .PublicationUnavailable
                                                 && ws.State ==
                                                 WebSocketState.Open)
                                        {
                                            await ws.CloseOutputAsync(
                                                WebSocketCloseStatus
                                                    .PolicyViolation,
                                                "Publication unavailable",
                                                CancellationToken.None);
                                        }
                                        break;
                                    }

                                    currentKey = accountId;
                                    publicationId =
                                        rebind.PublicationId;
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

    private async Task<WebSocketRebindResult>
        TryRebindConnectionAsync(
            string currentKey,
            string requestedKey,
            string deviceId,
            WebSocket ws,
            long? publicationId,
            PublicationReadContextService? publicationService,
            CancellationToken ct)
    {
        if (publicationService is null)
        {
            MoveConnection(
                currentKey,
                requestedKey,
                deviceId,
                ws,
                publicationId);
            return new WebSocketRebindResult(
                Rebound: true,
                publicationId,
                ChangedPublicationId: null,
                PublicationUnavailable: false);
        }

        PublicationReadLease? lease = null;
        try
        {
            lease =
                publicationService.PinningConfigured
                    ? await publicationService.AcquireAsync(ct)
                    : await publicationService
                        .AcquireWebSocketAdmissionAsync(ct);
            var pointers = lease.Pointers;
            if (!pointers.CurrentPublicationId.HasValue
                && (publicationId.HasValue
                    || publicationService
                        .PinningConfigured
                    || publicationService
                        .PublishedScopeSourceReadinessRequired))
            {
                return new WebSocketRebindResult(
                    Rebound: false,
                    PublicationId: null,
                    ChangedPublicationId: null,
                    PublicationUnavailable: true);
            }
            if (publicationId.HasValue
                && pointers.CurrentPublicationId !=
                    publicationId)
            {
                return new WebSocketRebindResult(
                    Rebound: false,
                    PublicationId: null,
                    pointers.CurrentPublicationId,
                    PublicationUnavailable: false);
            }

            if (publicationService
                    .PublishedScopeSourceReadinessRequired
                && (!pointers.PublishedScrapeId.HasValue
                    || !publicationService
                        .EvaluatePublishedScopeSourceReadiness(
                            pointers,
                            forceRefresh: true)
                        .Ready)
                || publicationService.PinningConfigured
                && (!pointers.PublishedScrapeId.HasValue
                    || !publicationService
                        .EvaluateReadiness(pointers)
                        .ReadyForPinning))
            {
                return new WebSocketRebindResult(
                    Rebound: false,
                    PublicationId: null,
                    ChangedPublicationId: null,
                    PublicationUnavailable: true);
            }

            await lease.VerifyHeldAsync(ct);
            MoveConnection(
                currentKey,
                requestedKey,
                deviceId,
                ws,
                pointers.CurrentPublicationId);
            return new WebSocketRebindResult(
                Rebound: true,
                pointers.CurrentPublicationId,
                ChangedPublicationId: null,
                PublicationUnavailable: false);
        }
        catch (Exception ex) when (
            ct.IsCancellationRequested
            && (ex is NpgsqlException
                or TimeoutException))
        {
            throw new OperationCanceledException(
                "WebSocket publication rebind was cancelled.",
                ex,
                ct);
        }
        catch (Exception ex) when (
            !ct.IsCancellationRequested
            && (ex is NpgsqlException
                or TimeoutException))
        {
            _log.LogWarning(
                ex,
                "WebSocket publication rebind could not acquire or validate the bounded shared publication lease for {CurrentAccountId}/{RequestedAccountId}/{DeviceId}.",
                currentKey,
                requestedKey,
                deviceId);
            return new WebSocketRebindResult(
                Rebound: false,
                PublicationId: null,
                ChangedPublicationId: null,
                PublicationUnavailable: true);
        }
        finally
        {
            if (lease is not null)
                await lease.DisposeAsync();
        }
    }

    private sealed record WebSocketRebindResult(
        bool Rebound,
        long? PublicationId,
        long? ChangedPublicationId,
        bool PublicationUnavailable);

    private void MoveConnection(
        string currentKey,
        string requestedKey,
        string deviceId,
        WebSocket ws,
        long? publicationId)
    {
        lock (_connectionMutationGate)
        {
            RemoveConnection(
                currentKey,
                deviceId,
                ws);
            AddConnection(
                requestedKey,
                deviceId,
                ws,
                publicationId);
        }
    }

    private object? BuildInitialSyncStatePayload(string accountId)
    {
        var liveProgress = _syncTracker?.GetProgress(accountId);
        if (liveProgress is not null && liveProgress.Phase != SyncProgressPhase.Queued)
        {
            if (!liveProgress.IsBackgroundRefresh
                && IsDurableBackgroundRefresh(accountId))
            {
                liveProgress.IsBackgroundRefresh = true;
            }
            return _syncTracker!.BuildPayloadForAccount(accountId, liveProgress);
        }

        var durablePayload = BuildDurableSyncStatePayload(accountId);
        if (durablePayload is not null)
            return durablePayload;

        return liveProgress is null ? null : _syncTracker!.BuildPayloadForAccount(accountId, liveProgress);
    }

    private bool IsDurableBackgroundRefresh(string accountId)
    {
        if (_metaDb is null)
            return false;

        var backfill = _metaDb.GetBackfillStatus(accountId);
        var history = _metaDb.GetHistoryReconStatus(accountId);
        var displayProgress = backfill is null
            ? null
            : _metaDb.GetBackfillSongProgress(
                accountId,
                backfill.SongsChecked,
                backfill.TotalSongsToCheck);
        return BackfillSyncClassification.IsBackgroundRefresh(
            backfill,
            history,
            displayProgress);
    }

    private object? BuildDurableSyncStatePayload(string accountId)
    {
        if (_metaDb is null) return null;

        var backfill = _metaDb.GetBackfillStatus(accountId);
        var history = _metaDb.GetHistoryReconStatus(accountId);
        var backfillDisplay = backfill is null
            ? null
            : _metaDb.GetBackfillSongProgress(
                accountId,
                backfill.SongsChecked,
                backfill.TotalSongsToCheck);
        var backgroundRefresh = BackfillSyncClassification.IsBackgroundRefresh(
            backfill,
            history,
            backfillDisplay);
        if (backfill is not null)
        {
            var backfillPayload = backfill.Status switch
            {
                "deferred" => BuildSyncProgressPayload(accountId, "queued", 0, backfill.TotalSongsToCheck, backfill.EntriesFound, displayItemsCompleted: 0, displayTotalItems: backfillDisplay?.TotalSongs, pendingRankUpdate: backfill.RankingsPending, backgroundRefresh: backgroundRefresh),
                "pending" or "in_progress" => BuildSyncProgressPayload(accountId, "backfill", backfill.SongsChecked, backfill.TotalSongsToCheck, backfill.EntriesFound, displayItemsCompleted: backfillDisplay?.SongsChecked, displayTotalItems: backfillDisplay?.TotalSongs, pendingRankUpdate: backfill.RankingsPending, backgroundRefresh: backgroundRefresh),
                "error" => BuildSyncProgressPayload(accountId, "error", backfill.SongsChecked, backfill.TotalSongsToCheck, backfill.EntriesFound, displayItemsCompleted: backfillDisplay?.SongsChecked, displayTotalItems: backfillDisplay?.TotalSongs, pendingRankUpdate: backfill.RankingsPending, backgroundRefresh: backgroundRefresh),
                _ => null,
            };
            if (backfillPayload is not null)
                return backfillPayload;
        }

        if (history?.Status is "in_progress")
            return BuildSyncProgressPayload(accountId, "history", history.SongsProcessed, history.TotalSongsToProcess, history.HistoryEntriesFound, seasonsQueried: history.SeasonsQueried, backgroundRefresh: backgroundRefresh);
        if (history?.Status is "error")
            return BuildSyncProgressPayload(accountId, "error", history.SongsProcessed, history.TotalSongsToProcess, history.HistoryEntriesFound, seasonsQueried: history.SeasonsQueried, backgroundRefresh: backgroundRefresh);

        var rivals = _metaDb.GetRivalsStatus(accountId);
        if (rivals?.Status is "pending" or "in_progress")
            return BuildSyncProgressPayload(accountId, "rivals", rivals.CombosComputed, rivals.TotalCombosToCompute, 0, rivalsFound: rivals.RivalsFound, backgroundRefresh: backgroundRefresh);
        if (rivals?.Status is "error")
            return BuildSyncProgressPayload(accountId, "error", rivals.CombosComputed, rivals.TotalCombosToCompute, 0, rivalsFound: rivals.RivalsFound, backgroundRefresh: backgroundRefresh);

        if (backfill?.Status is "complete")
        {
            return BuildSyncProgressPayload(accountId, "complete", backfill.TotalSongsToCheck, backfill.TotalSongsToCheck, backfill.EntriesFound, displayItemsCompleted: backfillDisplay?.TotalSongs, displayTotalItems: backfillDisplay?.TotalSongs, pendingRankUpdate: backfill.RankingsPending, backgroundRefresh: backgroundRefresh);
        }
        if (history?.Status is "complete")
            return BuildSyncProgressPayload(accountId, "complete", history.TotalSongsToProcess, history.TotalSongsToProcess, history.HistoryEntriesFound, seasonsQueried: history.SeasonsQueried, pendingRankUpdate: backfill?.RankingsPending, backgroundRefresh: backgroundRefresh);

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
        bool? pendingRankUpdate = null,
        bool backgroundRefresh = false)
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
            backgroundRefresh,
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
        long? currentPublicationId,
        bool forceRefresh = false)
    {
        if (!currentPublicationId.HasValue
            || (!forceRefresh
                && connectionPublicationId.HasValue
                && connectionPublicationId.Value
                        == currentPublicationId.Value))
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
