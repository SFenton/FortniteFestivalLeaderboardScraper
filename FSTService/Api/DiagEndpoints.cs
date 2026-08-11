using System.Text.Json;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    public static void MapDiagEndpoints(this WebApplication app)
    {
        // ─── Diagnostic: snapshot in-flight HTTP operations + proxy slots ────────────
        // Used to diagnose scrape stalls where the leaderboard count is stuck a few
        // short of total. Returns every in-flight SendAsync call with its current
        // state (waiting_for_cdn_clear / backoff_delay / acquiring_rate_token /
        // sending / reading_body), attempt number, age, and per-proxy slot health.
        //
        // Ops stuck with state=sending for > 30s after a VPN recycle are the prime
        // suspects: they're pinned to a zombie TCP socket that survived CancelInflight()
        // + ResetConnectionPool because the request had already returned response
        // headers and was awaiting body bytes on a silently-dead connection.

        app.MapGet("/api/diag/inflight", (
            GlobalLeaderboardScraper scraper,
            ProxyHandlerAccessor proxyAccessor) =>
        {
            var now = DateTimeOffset.UtcNow;
            var ops = scraper.Executor.InflightOperations
                .Select(o => new
                {
                    operationId = o.OperationId,
                    label = o.Label,
                    state = o.State.ToString(),
                    attempt = o.Attempt,
                    statusAttempts = o.StatusAttempts,
                    networkErrors = o.NetworkErrors,
                    startedAtUtc = o.StartedAt,
                    stateEnteredAtUtc = o.StateEnteredAt,
                    ageSeconds = (now - o.StartedAt).TotalSeconds,
                    stateAgeSeconds = (now - o.StateEnteredAt).TotalSeconds,
                })
                .ToList();

            var proxySlots = proxyAccessor.Handler?.GetSlotSnapshots()
                .Select(s => new
                {
                    url = s.Url,
                    isInCooldown = s.IsInCooldown,
                    cooldownUntilUtc = s.CooldownUntil,
                    cooldownRemainingSeconds = s.IsInCooldown
                        ? (s.CooldownUntil - now).TotalSeconds
                        : 0,
                    isReconnecting = s.IsReconnecting,
                })
                .ToList<object>();

            return Results.Ok(new
            {
                timestampUtc = now,
                totalInflight = ops.Count,
                oldestAgeSeconds = ops.Count > 0 ? ops.Max(o => o.ageSeconds) : 0,
                stuckCount = ops.Count(o => o.stateAgeSeconds > 30),
                cdnBlocked = scraper.Executor.IsCdnBlocked,
                cdnProbeAttempts = scraper.Executor.CdnProbeAttempts,
                cdnProbeSuccesses = scraper.Executor.CdnProbeSuccesses,
                cdnBlocksDetected = scraper.Executor.CdnBlocksDetected,
                totalHttpSends = scraper.Executor.TotalHttpSends,
                proxySlots,
                inflightOperations = ops,
            });
        })
        .WithTags("Diagnostic")
        .RequireAuthorization()
        .RequireRateLimiting("protected");

        app.MapGet("/api/diag/improvement-notifications", (
            [FromServices] ImprovementNotificationService notifications,
            [FromServices] IOptions<ImprovementNotificationOptions> notificationOptions) =>
        {
            var publication = notifications.GetPublicationStatus();
            var staleness = ImprovementNotificationStalenessEvaluator.Evaluate(
                publication,
                notificationOptions.Value,
                DateTime.UtcNow);
            return Results.Ok(new { publication, staleness });
        })
        .WithTags("Diagnostic")
        .RequireAuthorization()
        .RequireRateLimiting("protected");

        app.MapPost("/api/debug/client-interactions", (
            ClientInteractionTelemetryBatch batch,
            HttpContext httpContext,
            IOptions<ClientTelemetryOptions> telemetryOptions,
            ILoggerFactory loggerFactory) =>
        {
            var options = telemetryOptions.Value;
            if (!options.Enabled)
                return Results.NotFound(new { error = "Client interaction telemetry is disabled." });

            if (httpContext.Request.ContentLength is > 0 && httpContext.Request.ContentLength > options.MaxPayloadBytes)
                return Results.BadRequest(new { error = "Telemetry payload is too large." });

            if (batch.Events.Count == 0)
                return Results.BadRequest(new { error = "Telemetry batch must contain at least one event." });

            if (batch.Events.Count > options.MaxEventsPerBatch)
                return Results.BadRequest(new { error = "Telemetry batch contains too many events.", maxEvents = options.MaxEventsPerBatch });

            var logger = loggerFactory.CreateLogger("FSTService.ClientInteractionTelemetry");
            var recordsJson = JsonSerializer.Serialize(batch.Events, ClientTelemetryLogJsonOptions);
            logger.LogInformation(
                "Client interaction telemetry accepted: session={SessionId} route={Route} events={EventCount} viewport={ViewportWidth}x{ViewportHeight} recordsJson={RecordsJson}",
                Truncate(batch.SessionId, 64),
                Truncate(batch.Route, 128),
                batch.Events.Count,
                batch.Viewport?.Width,
                batch.Viewport?.Height,
                recordsJson);

            return Results.Ok(new { accepted = true, eventCount = batch.Events.Count });
        })
        .WithTags("Diagnostic")
        .RequireRateLimiting("public");
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
        return value[..maxLength];
    }

    private static readonly JsonSerializerOptions ClientTelemetryLogJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed class ClientInteractionTelemetryBatch
{
    public string? SessionId { get; set; }
    public string? CapturedAtUtc { get; set; }
    public string? Route { get; set; }
    public ClientInteractionTelemetryViewport? Viewport { get; set; }
    public List<ClientInteractionTelemetryRecord> Events { get; set; } = [];
}

public sealed class ClientInteractionTelemetryViewport
{
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? DevicePixelRatio { get; set; }
    public double? VisualViewportWidth { get; set; }
    public double? VisualViewportHeight { get; set; }
    public double? VisualViewportOffsetTop { get; set; }
}

public sealed class ClientInteractionTelemetryRecord
{
    public string Kind { get; set; } = "event";
    public string? EventType { get; set; }
    public string? Label { get; set; }
    public string? Phase { get; set; }
    public Dictionary<string, JsonElement>? Details { get; set; }
    public double Time { get; set; }
    public double? ClientX { get; set; }
    public double? ClientY { get; set; }
    public int? Button { get; set; }
    public string? PointerType { get; set; }
    public ClientInteractionTelemetryElement? Target { get; set; }
    public ClientInteractionTelemetryElement? HitTarget { get; set; }
    public List<ClientInteractionTelemetryElement> Path { get; set; } = [];
    public Dictionary<string, JsonElement>? State { get; set; }
}

public sealed class ClientInteractionTelemetryElement
{
    public string? Tag { get; set; }
    public string? Id { get; set; }
    public string? TestId { get; set; }
    public string? Role { get; set; }
    public string? ClassName { get; set; }
    public string? PointerEvents { get; set; }
    public string? Display { get; set; }
    public string? Visibility { get; set; }
    public string? Position { get; set; }
    public string? ZIndex { get; set; }
}
