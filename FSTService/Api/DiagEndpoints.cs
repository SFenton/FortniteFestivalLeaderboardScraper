using System.Net.Http.Headers;
using System.Text.Json;
using FSTService.Auth;
using FSTService.Scraping;
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
        .RequireRateLimiting("public");

        // ─── Diagnostic: query FNFestival events ────────────

        app.MapGet("/api/diag/events", async (
            TokenManager tokenManager,
            IHttpClientFactory httpFactory,
            string? gameId) =>
        {
            gameId ??= "FNFestival";
            var accessToken = await tokenManager.GetAccessTokenAsync();
            if (accessToken is null)
                return Results.Problem("No access token available");

            var accountId = tokenManager.AccountId!;
            var url = $"https://events-public-service-live.ol.epicgames.com/api/v1/events/{gameId}/data/{accountId}?showPastEvents=true";

            using var http = httpFactory.CreateClient();
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var res = await http.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();

            return Results.Content(body, "application/json", statusCode: (int)res.StatusCode);
        })
        .WithTags("Diagnostic")
        .RequireRateLimiting("public");

        // ─── Diagnostic: test arbitrary leaderboard URL pattern ────────────

        app.MapGet("/api/diag/leaderboard", async (
            HttpContext context,
            TokenManager tokenManager,
            IHttpClientFactory httpFactory,
            string eventId,
            string windowId,
            int? version,
            string? gameId,
            string? acct,
            int? fromIndex,
            string? findTeams,
            int? page,
            int? rank,
            string? teamAccountIds) =>
        {
            gameId ??= "FNFestival";
            var accessToken = await tokenManager.GetAccessTokenAsync();
            if (accessToken is null)
                return Results.Problem("No access token available");

            var accountId = tokenManager.AccountId!;
            using var http = httpFactory.CreateClient();

            if (version == 2)
            {
                // V2: POST — build query string from optional params
                var qs = new List<string>();
                if (acct != "false") // acct=false to omit accountId
                    qs.Add($"accountId={accountId}");
                qs.Add($"fromIndex={fromIndex ?? 0}");
                if (findTeams != null)
                    qs.Add($"findTeams={findTeams}");

                var qsStr = string.Join("&", qs);
                var url = $"https://events-public-service-live.ol.epicgames.com/api/v2/games/{gameId}/leaderboards/{eventId}/{windowId}/scores?{qsStr}";
                var teamsJson = string.IsNullOrEmpty(teamAccountIds)
                    ? "{\"teams\":[]}"
                    : $"{{\"teams\":[[\"{teamAccountIds}\"]]}}";
                var body = new System.Net.Http.StringContent(teamsJson, System.Text.Encoding.UTF8, "application/json");
                var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                req.Content = body;
                var res = await http.SendAsync(req);
                var respBody = await res.Content.ReadAsStringAsync();
                return Results.Content($"{{\"_url\":\"{url}\",\"_status\":{(int)res.StatusCode},\"body\":{respBody}}}", "application/json", statusCode: 200);
            }
            else
            {
                var p = page ?? 0;
                var r = rank ?? 0;
                var teamPart = string.IsNullOrEmpty(teamAccountIds) ? "" : $"&teamAccountIds={teamAccountIds}";
                var url = $"https://events-public-service-live.ol.epicgames.com/api/v1/leaderboards/{gameId}/{eventId}/{windowId}/{accountId}?page={p}&rank={r}{teamPart}&appId=Fortnite&showLiveSessions=false";
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                var res = await http.SendAsync(req);
                var respBody = await res.Content.ReadAsStringAsync();
                return Results.Content($"{{\"_url\":\"{url}\",\"_status\":{(int)res.StatusCode},\"body\":{respBody}}}", "application/json", statusCode: 200);
            }
        })
        .WithTags("Diagnostic")
        .RequireRateLimiting("public");

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
