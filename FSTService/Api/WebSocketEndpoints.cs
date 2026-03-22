using System.Net.WebSockets;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    public static void MapWebSocketEndpoints(this WebApplication app)
    {
        // WebSocket endpoint for real-time notifications (shop changes, backfill, etc.)
        // Clients connect via GET /api/ws — no auth required for web clients.
        // Authenticated mobile clients can pass ?token={jwt}&deviceId={id} for per-account notifications.
        app.Map("/api/ws", async (
            HttpContext context,
            NotificationService notifications) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("WebSocket connections only.");
                return;
            }

            var ws = await context.WebSockets.AcceptWebSocketAsync();

            // For authenticated clients, use their account+device for per-account notifications.
            // For anonymous web clients, use a generated ID — they still receive global broadcasts.
            var accountId = context.User.Identity?.IsAuthenticated == true
                ? context.User.FindFirst("sub")?.Value ?? context.User.FindFirst("accountId")?.Value
                : null;
            var deviceId = context.Request.Query["deviceId"].FirstOrDefault();

            var effectiveAccountId = accountId ?? $"anon-{Guid.NewGuid():N}";
            var effectiveDeviceId = deviceId ?? $"web-{Guid.NewGuid():N}";

            await notifications.HandleConnectionAsync(
                effectiveAccountId, effectiveDeviceId, ws, context.RequestAborted);
        });
    }
}
