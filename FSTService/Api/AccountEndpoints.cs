using FSTService.Persistence;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app)
    {
        // Check if an account exists by username (used by mobile app before login)
        app.MapGet("/api/account/check", (string username, MetaDatabase metaDb) =>
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
        app.MapGet("/api/account/search", (string q, int? limit, MetaDatabase metaDb) =>
        {
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
}
