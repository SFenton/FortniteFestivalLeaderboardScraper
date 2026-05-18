using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.AspNetCore.Mvc;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app)
    {
        app.MapPost("/api/account/name-refresh", async (
            HttpContext httpContext,
            AccountNameRefreshRequest request,
            AccountNameRefreshService refreshService,
            [FromKeyedServices("PlayerCache")] ResponseCacheService playerCache,
            [FromKeyedServices("NeighborhoodCache")] ResponseCacheService neighborhoodCache,
            [FromKeyedServices("RivalsCache")] ResponseCacheService rivalsCache,
            [FromKeyedServices("LeaderboardRivalsCache")] ResponseCacheService leaderboardRivalsCache,
            CancellationToken ct) =>
        {
            httpContext.Response.Headers.CacheControl = "no-cache, no-store";

            var accountIds = AccountNameRefreshService.NormalizeAccountIds(request.AccountIds);
            if (accountIds.Count == 0)
                return Results.Ok(AccountNameRefreshResult.Empty);
            if (accountIds.Count > AccountNameRefreshService.MaxAccountIdsPerRequest)
                return Results.BadRequest(new
                {
                    error = $"A maximum of {AccountNameRefreshService.MaxAccountIdsPerRequest} account IDs can be refreshed at once.",
                });

            var result = await refreshService.RefreshAsync(accountIds, ct);
            if (result.ChangedAccountIds.Count > 0)
            {
                InvalidateAccountNameCaches(result.ChangedAccountIds, playerCache, neighborhoodCache, rivalsCache, leaderboardRivalsCache);
            }

            return Results.Ok(result);
        })
        .WithTags("Account")
        .RequireRateLimiting("public");

        // Check if an account exists by username (used by mobile app before login)
        app.MapGet("/api/account/check", (string username, IMetaDatabase metaDb) =>
        {
            if (string.IsNullOrWhiteSpace(username))
                return Results.BadRequest(new { error = "username query parameter is required." });

            var accountId = metaDb.GetAccountIdForUsername(username.Trim());
            return Results.Ok(new
            {
                exists = accountId is not null,
                accountId,
                displayName = accountId is not null ? metaDb.GetDisplayName(accountId) : null,
            });
        })
        .WithTags("Account")
        .RequireRateLimiting("public");

        // Search account display names (autocomplete)
        app.MapGet("/api/account/search", (HttpContext httpContext, string q, int? limit, IMetaDatabase metaDb) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=60";
            if (string.IsNullOrWhiteSpace(q))
                return Results.Ok(new { results = Array.Empty<object>() });

            var matches = metaDb.SearchAccountNames(q.Trim(), Math.Min(limit ?? 10, 50));
            return Results.Ok(new
            {
                results = matches.Select(m => new
                {
                    accountId = m.AccountId,
                    displayName = m.DisplayName,
                }).ToList()
            });
        })
        .WithTags("Account")
        .RequireRateLimiting("public");
    }

    private static void InvalidateAccountNameCaches(IReadOnlyCollection<string> accountIds, params ResponseCacheService[] caches)
    {
        foreach (var cache in caches)
        {
            cache.InvalidateWhere(key => accountIds.Any(accountId => key.Contains(accountId, StringComparison.OrdinalIgnoreCase)));
        }
    }
}

public sealed record AccountNameRefreshRequest(string[]? AccountIds);
