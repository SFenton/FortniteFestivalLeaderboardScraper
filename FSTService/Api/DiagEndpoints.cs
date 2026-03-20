using System.Net.Http.Headers;
using FSTService.Auth;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    public static void MapDiagEndpoints(this WebApplication app)
    {
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
    }
}
