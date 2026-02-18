using System.Net.WebSockets;
using System.Text;
using FSTService.Api;
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
    public async Task NotifyPersonalDbReadyAsync_SendsCorrectType()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);

        svc.AddConnection("acct1", "dev1", ws);

        await svc.NotifyPersonalDbReadyAsync("acct1");

        await ws.Received(1).SendAsync(
            Arg.Is<ArraySegment<byte>>(seg =>
                Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count).Contains("personal_db_ready")),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }

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
}
