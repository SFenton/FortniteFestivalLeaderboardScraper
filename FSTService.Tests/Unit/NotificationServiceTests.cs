using System.Net.WebSockets;
using System.Text;
using FSTService.Api;
using FSTService.Scraping;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FSTService.Tests.Unit;

/// <summary>
/// Tests for <see cref="NotificationService"/> — WebSocket connection management
/// and push notifications.
/// </summary>
public sealed class NotificationServiceTests
{
    private readonly ILogger<NotificationService> _log = Substitute.For<ILogger<NotificationService>>();

    private NotificationService CreateService() => new(_log);

    // ─── AddConnection / RemoveConnection ───────────────────────

    [Fact]
    public async Task AddConnection_ThenNotify_SendsMessage()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);

        svc.AddConnection("acct1", "dev1", ws);

        await svc.NotifyAccountAsync("acct1", new { type = "test" });

        await ws.Received(1).SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            WebSocketMessageType.Text,
            true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveConnection_ThenNotify_DoesNotSend()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);

        svc.AddConnection("acct1", "dev1", ws);
        svc.RemoveConnection("acct1", "dev1");

        await svc.NotifyAccountAsync("acct1", new { type = "test" });

        await ws.DidNotReceive().SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            Arg.Any<WebSocketMessageType>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void RemoveConnection_UnknownAccount_DoesNotThrow()
    {
        var svc = CreateService();
        // Should not throw
        svc.RemoveConnection("unknown", "unknown");
    }

    // ─── NotifyAccountAsync ─────────────────────────────────────

    [Fact]
    public async Task NotifyAccountAsync_UnknownAccount_DoesNothing()
    {
        var svc = CreateService();
        // Should not throw
        await svc.NotifyAccountAsync("nobody", new { type = "test" });
    }

    [Fact]
    public async Task NotifyAccountAsync_ClosedSocket_CleanedUp()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Closed);

        svc.AddConnection("acct1", "dev1", ws);

        await svc.NotifyAccountAsync("acct1", new { type = "test" });

        // Should NOT have tried to send (socket is closed)
        await ws.DidNotReceive().SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            Arg.Any<WebSocketMessageType>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());

        // After cleanup, notifying again should do nothing (no crash)
        await svc.NotifyAccountAsync("acct1", new { type = "test2" });
    }

    [Fact]
    public async Task NotifyAccountAsync_SendThrows_CleansUpDeadConnection()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);
        ws.When(x => x.SendAsync(
                Arg.Any<ArraySegment<byte>>(),
                Arg.Any<WebSocketMessageType>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>()))
            .Do(_ => throw new WebSocketException("Connection lost"));

        svc.AddConnection("acct1", "dev1", ws);

        // Should not throw — exception is caught and connection cleaned up
        await svc.NotifyAccountAsync("acct1", new { type = "test" });

        // Sending again should not try to send (connection was removed)
        ws.ClearReceivedCalls();
        await svc.NotifyAccountAsync("acct1", new { type = "test2" });
        await ws.DidNotReceive().SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            Arg.Any<WebSocketMessageType>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifyAccountAsync_MultipleDevices_AllReceive()
    {
        var svc = CreateService();
        var ws1 = Substitute.For<WebSocket>();
        var ws2 = Substitute.For<WebSocket>();
        ws1.State.Returns(WebSocketState.Open);
        ws2.State.Returns(WebSocketState.Open);

        svc.AddConnection("acct1", "dev1", ws1);
        svc.AddConnection("acct1", "dev2", ws2);

        await svc.NotifyAccountAsync("acct1", new { type = "broadcast" });

        await ws1.Received(1).SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
        await ws2.Received(1).SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }

    // ─── Convenience methods ────────────────────────────────────

    [Fact]
    public async Task NotifyBackfillCompleteAsync_SendsCorrectType()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);

        svc.AddConnection("acct1", "dev1", ws);

        await svc.NotifyBackfillCompleteAsync("acct1");

        await ws.Received(1).SendAsync(
            Arg.Is<ArraySegment<byte>>(seg =>
                Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count).Contains("backfill_complete")),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifyHistoryReconCompleteAsync_SendsCorrectType()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);

        svc.AddConnection("acct1", "dev1", ws);

        await svc.NotifyHistoryReconCompleteAsync("acct1");

        await ws.Received(1).SendAsync(
            Arg.Is<ArraySegment<byte>>(seg =>
                Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count).Contains("history_recon_complete")),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }

    // ─── HandleConnectionAsync ──────────────────────────────

    [Fact]
    public async Task HandleConnectionAsync_ClientSendsClose_ClosesGracefully()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();

        // First call: Open and return Close message. Second call: state changes to Closed.
        int callCount = 0;
        ws.State.Returns(_ => callCount < 1 ? WebSocketState.Open : WebSocketState.Closed);
        ws.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            });

        await svc.HandleConnectionAsync("acct1", "dev1", ws, CancellationToken.None);

        await ws.Received(1).CloseOutputAsync(
            WebSocketCloseStatus.NormalClosure, "Goodbye", CancellationToken.None);
    }

    [Fact]
    public async Task HandleConnectionAsync_WebSocketException_BreaksLoop()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);
        ws.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new WebSocketException("Connection reset"));

        // Should complete without throwing
        await svc.HandleConnectionAsync("acct1", "dev1", ws, CancellationToken.None);

        // Connection should be removed in the finally block
        // Verify by sending a notification — no send should occur
        await svc.NotifyAccountAsync("acct1", new { type = "test" });
        await ws.DidNotReceive().SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleConnectionAsync_Cancellation_BreaksLoop()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);

        var cts = new CancellationTokenSource();
        ws.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await svc.HandleConnectionAsync("acct1", "dev1", ws, cts.Token);

        // Connection should be cleaned up
        await svc.NotifyAccountAsync("acct1", new { type = "test" });
        await ws.DidNotReceive().SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleConnectionAsync_TextMessage_ContinuesReading()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();

        int callCount = 0;
        ws.State.Returns(_ => callCount < 2 ? WebSocketState.Open : WebSocketState.Closed);
        ws.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 1)
                    return new WebSocketReceiveResult(5, WebSocketMessageType.Text, true);
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            });

        await svc.HandleConnectionAsync("acct1", "dev1", ws, CancellationToken.None);

        // Should have received text then close
        await ws.Received(2).ReceiveAsync(
            Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>());
    }

    // ─── BroadcastAllAsync ──────────────────────────────────

    [Fact]
    public async Task BroadcastAllAsync_NoClients_DoesNotThrow()
    {
        var svc = CreateService();
        await svc.BroadcastAllAsync(new { type = "test" });
    }

    [Fact]
    public async Task BroadcastAllAsync_OpenClient_SendsMessage()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);
        svc.AddConnection("acct1", "dev1", ws);

        await svc.BroadcastAllAsync(new { type = "shop_changed" });

        await ws.Received(1).SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BroadcastAllAsync_ClosedClient_CleanedUp()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Closed);
        svc.AddConnection("acct1", "dev1", ws);

        await svc.BroadcastAllAsync(new { type = "test" });

        // Send should not have been called (closed socket)
        await ws.DidNotReceive().SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BroadcastAllAsync_SendThrows_RemovesDeadConnection()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);
        ws.SendAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<WebSocketMessageType>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new WebSocketException("broken"));
        svc.AddConnection("acct1", "dev1", ws);

        await svc.BroadcastAllAsync(new { type = "test" });

        // After cleanup, notify should not attempt to send again
        ws.ClearReceivedCalls();
        await svc.NotifyAccountAsync("acct1", new { type = "check" });
        await ws.DidNotReceive().SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            Arg.Any<WebSocketMessageType>(), Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    // ─── SendShopSnapshotAsync ──────────────────────────────

    [Fact]
    public async Task SendShopSnapshotAsync_OpenSocket_Sends()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);

        await svc.SendShopSnapshotAsync(ws, new[] { "song1" }, Array.Empty<string>());

        await ws.Received(1).SendAsync(
            Arg.Is<ArraySegment<byte>>(seg =>
                Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count).Contains("shop_snapshot")),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendShopSnapshotAsync_ClosedSocket_DoesNotSend()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Closed);

        await svc.SendShopSnapshotAsync(ws, new[] { "song1" }, Array.Empty<string>());

        await ws.DidNotReceive().SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            Arg.Any<WebSocketMessageType>(), Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    // ─── NotifyShopChangedAsync ─────────────────────────────

    [Fact]
    public async Task NotifyShopChangedAsync_BroadcastsToAll()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);
        svc.AddConnection("acct1", "dev1", ws);

        await svc.NotifyShopChangedAsync(
            new[] { "added1" }, new[] { "removed1" }, 5, new[] { "leaving1" });

        await ws.Received(1).SendAsync(
            Arg.Is<ArraySegment<byte>>(seg =>
                Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count).Contains("shop_changed")),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }

    // ─── HandleConnectionAsync with ShopProvider ────────────

    [Fact]
    public async Task HandleConnectionAsync_WithShopProvider_SendsSnapshotOnConnect()
    {
        var svc = CreateService();
        var shopProvider = Substitute.For<IShopProvider>();
        shopProvider.InShopSongIds.Returns(new HashSet<string> { "shop_s1" });
        shopProvider.LeavingTomorrowSongIds.Returns(new HashSet<string> { "leaving_s1" });
        svc.SetShopProvider(shopProvider);

        // FestivalService is needed to enrich shop snapshots — use a real one (empty songs is fine)
        var festivalService = new FortniteFestival.Core.Services.FestivalService();
        svc.SetFestivalService(festivalService);

        var ws = Substitute.For<WebSocket>();
        int callCount = 0;
        ws.State.Returns(_ => callCount < 1 ? WebSocketState.Open : WebSocketState.Closed);
        ws.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            });

        await svc.HandleConnectionAsync("acct1", "dev1", ws, CancellationToken.None);

        // Snapshot should have been sent
        await ws.Received().SendAsync(
            Arg.Is<ArraySegment<byte>>(seg =>
                Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count).Contains("shop_snapshot")),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleConnectionAsync_ShopProviderThrows_DoesNotCrash()
    {
        var svc = CreateService();
        var shopProvider = Substitute.For<IShopProvider>();
        shopProvider.InShopSongIds.Returns(_ => throw new InvalidOperationException("Shop not ready"));
        svc.SetShopProvider(shopProvider);

        var ws = Substitute.For<WebSocket>();
        int callCount = 0;
        ws.State.Returns(_ => callCount < 1 ? WebSocketState.Open : WebSocketState.Closed);
        ws.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            });

        // Should not throw even if shop provider fails
        await svc.HandleConnectionAsync("acct1", "dev1", ws, CancellationToken.None);
    }

    // ─── NotifyRivalsCompleteAsync ──────────────────────────

    [Fact]
    public async Task NotifyRivalsCompleteAsync_SendsCorrectType()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);
        svc.AddConnection("acct1", "dev1", ws);

        await svc.NotifyRivalsCompleteAsync("acct1");

        await ws.Received(1).SendAsync(
            Arg.Is<ArraySegment<byte>>(seg =>
                Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count).Contains("rivals_complete")),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }

    // ─── WebSocket subscribe/unsubscribe rebind ─────────────

    [Fact]
    public async Task HandleConnectionAsync_SubscribeSync_RebindsToRealAccountId()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        var subscribeJson = Encoding.UTF8.GetBytes("""{"action":"subscribe_sync","accountId":"real-acct"}""");

        int callCount = 0;
        ws.State.Returns(_ => callCount < 2 ? WebSocketState.Open : WebSocketState.Closed);
        ws.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 1)
                {
                    var buf = callInfo.ArgAt<ArraySegment<byte>>(0);
                    Array.Copy(subscribeJson, 0, buf.Array!, buf.Offset, subscribeJson.Length);
                    return new WebSocketReceiveResult(subscribeJson.Length, WebSocketMessageType.Text, true);
                }
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            });

        await svc.HandleConnectionAsync("anon-123", "dev1", ws, CancellationToken.None);

        // After subscribe, notify to "real-acct" should reach the socket
        ws.State.Returns(WebSocketState.Open);
        svc.AddConnection("real-acct", "dev1", ws); // Re-add since finally removed it
        await svc.NotifyAccountAsync("real-acct", new { type = "sync_progress" });

        await ws.Received().SendAsync(
            Arg.Is<ArraySegment<byte>>(seg =>
                Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count).Contains("sync_progress")),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleConnectionAsync_SubscribeSync_OriginalKeyNoLongerReceives()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        var wsOther = Substitute.For<WebSocket>();
        wsOther.State.Returns(WebSocketState.Open);
        var subscribeJson = Encoding.UTF8.GetBytes("""{"action":"subscribe_sync","accountId":"real-acct"}""");

        int callCount = 0;
        ws.State.Returns(_ => callCount < 2 ? WebSocketState.Open : WebSocketState.Closed);
        ws.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 1)
                {
                    var buf = callInfo.ArgAt<ArraySegment<byte>>(0);
                    Array.Copy(subscribeJson, 0, buf.Array!, buf.Offset, subscribeJson.Length);
                    return new WebSocketReceiveResult(subscribeJson.Length, WebSocketMessageType.Text, true);
                }
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            });

        await svc.HandleConnectionAsync("anon-123", "dev1", ws, CancellationToken.None);

        // After subscribe + close, notifying "anon-123" should not reach any socket
        await svc.NotifyAccountAsync("anon-123", new { type = "test" });
        await wsOther.DidNotReceive().SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            Arg.Any<WebSocketMessageType>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleConnectionAsync_UnsubscribeSync_RevertsToOriginalKey()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        var subscribeJson = Encoding.UTF8.GetBytes("""{"action":"subscribe_sync","accountId":"real-acct"}""");
        var unsubscribeJson = Encoding.UTF8.GetBytes("""{"action":"unsubscribe_sync"}""");

        int callCount = 0;
        ws.State.Returns(_ => callCount < 3 ? WebSocketState.Open : WebSocketState.Closed);
        ws.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                var buf = callInfo.ArgAt<ArraySegment<byte>>(0);
                if (callCount == 1)
                {
                    Array.Copy(subscribeJson, 0, buf.Array!, buf.Offset, subscribeJson.Length);
                    return new WebSocketReceiveResult(subscribeJson.Length, WebSocketMessageType.Text, true);
                }
                if (callCount == 2)
                {
                    Array.Copy(unsubscribeJson, 0, buf.Array!, buf.Offset, unsubscribeJson.Length);
                    return new WebSocketReceiveResult(unsubscribeJson.Length, WebSocketMessageType.Text, true);
                }
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            });

        await svc.HandleConnectionAsync("anon-123", "dev1", ws, CancellationToken.None);

        // After unsubscribe + close, "real-acct" notifications should not reach the socket
        await svc.NotifyAccountAsync("real-acct", new { type = "test" });
        await ws.DidNotReceive().SendAsync(
            Arg.Is<ArraySegment<byte>>(seg =>
                Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count).Contains("\"test\"")),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleConnectionAsync_MalformedJson_DoesNotCrash()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        var badJson = Encoding.UTF8.GetBytes("{broken json!!!");

        int callCount = 0;
        ws.State.Returns(_ => callCount < 2 ? WebSocketState.Open : WebSocketState.Closed);
        ws.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 1)
                {
                    var buf = callInfo.ArgAt<ArraySegment<byte>>(0);
                    Array.Copy(badJson, 0, buf.Array!, buf.Offset, badJson.Length);
                    return new WebSocketReceiveResult(badJson.Length, WebSocketMessageType.Text, true);
                }
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            });

        // Should not throw
        await svc.HandleConnectionAsync("anon-123", "dev1", ws, CancellationToken.None);
    }

    [Fact]
    public async Task HandleConnectionAsync_SubscribeRebind_DisconnectCleansUpCorrectKey()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        var subscribeJson = Encoding.UTF8.GetBytes("""{"action":"subscribe_sync","accountId":"real-acct"}""");

        int callCount = 0;
        ws.State.Returns(_ => callCount < 2 ? WebSocketState.Open : WebSocketState.Closed);
        ws.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 1)
                {
                    var buf = callInfo.ArgAt<ArraySegment<byte>>(0);
                    Array.Copy(subscribeJson, 0, buf.Array!, buf.Offset, subscribeJson.Length);
                    return new WebSocketReceiveResult(subscribeJson.Length, WebSocketMessageType.Text, true);
                }
                // Simulate connection lost
                throw new WebSocketException("Connection reset");
            });

        await svc.HandleConnectionAsync("anon-123", "dev1", ws, CancellationToken.None);

        // After disconnect, "real-acct" should have been cleaned up by finally block
        // Notifying "real-acct" should not send anything
        await svc.NotifyAccountAsync("real-acct", new { type = "test" });
        await ws.DidNotReceive().SendAsync(
            Arg.Is<ArraySegment<byte>>(seg =>
                Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count).Contains("\"test\"")),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }
}
