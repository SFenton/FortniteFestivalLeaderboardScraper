using FSTService.Persistence;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    public static void MapRankingsEndpoints(this WebApplication app)
    {
        // ─── Per-instrument rankings (paginated) ───────────────

        app.MapGet("/api/rankings/{instrument}", (
            HttpContext httpContext,
            string instrument,
            string? rankBy,
            int? page,
            int? pageSize,
            GlobalLeaderboardPersistence persistence,
            MetaDatabase metaDb) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=1800, stale-while-revalidate=3600";
            var db = persistence.GetOrCreateInstrumentDb(instrument);
            var (entries, total) = db.GetAccountRankings(
                rankBy ?? "adjusted",
                page ?? 1,
                Math.Clamp(pageSize ?? 50, 1, 200));

            // Bulk resolve display names (single DB call)
            var names = metaDb.GetDisplayNames(entries.Select(e => e.AccountId));

            var enriched = entries.Select(e => new
            {
                e.AccountId,
                displayName = names.GetValueOrDefault(e.AccountId),
                e.SongsPlayed,
                e.TotalChartedSongs,
                e.Coverage,
                e.RawSkillRating,
                e.AdjustedSkillRating,
                e.AdjustedSkillRank,
                e.WeightedRating,
                e.WeightedRank,
                e.FcRate,
                e.FcRateRank,
                e.TotalScore,
                e.TotalScoreRank,
                e.MaxScorePercent,
                e.MaxScorePercentRank,
                e.AvgAccuracy,
                e.FullComboCount,
                e.AvgStars,
                e.BestRank,
                e.AvgRank,
                e.ComputedAt,
            }).ToList();

            return Results.Ok(new
            {
                instrument,
                rankBy = rankBy ?? "adjusted",
                page = page ?? 1,
                pageSize = Math.Clamp(pageSize ?? 50, 1, 200),
                totalAccounts = total,
                entries = enriched,
            });
        })
        .WithTags("Rankings")
        .RequireRateLimiting("public");

        // ─── Single account per-instrument ranking ─────────────

        app.MapGet("/api/rankings/{instrument}/{accountId}", (
            string instrument,
            string accountId,
            GlobalLeaderboardPersistence persistence,
            MetaDatabase metaDb) =>
        {
            var db = persistence.GetOrCreateInstrumentDb(instrument);
            var ranking = db.GetAccountRanking(accountId);
            if (ranking is null)
                return Results.NotFound(new { error = "Account not found in rankings for this instrument." });

            var totalRanked = db.GetRankedAccountCount();

            return Results.Ok(new
            {
                ranking.AccountId,
                displayName = metaDb.GetDisplayName(accountId),
                ranking.Instrument,
                ranking.SongsPlayed,
                ranking.TotalChartedSongs,
                ranking.Coverage,
                ranking.RawSkillRating,
                ranking.AdjustedSkillRating,
                ranking.AdjustedSkillRank,
                ranking.WeightedRating,
                ranking.WeightedRank,
                ranking.FcRate,
                ranking.FcRateRank,
                ranking.TotalScore,
                ranking.TotalScoreRank,
                ranking.MaxScorePercent,
                ranking.MaxScorePercentRank,
                ranking.AvgAccuracy,
                ranking.FullComboCount,
                ranking.AvgStars,
                ranking.BestRank,
                ranking.AvgRank,
                ranking.ComputedAt,
                totalRankedAccounts = totalRanked,
            });
        })
        .WithTags("Rankings")
        .RequireRateLimiting("public");

        // ─── Rank history for a player on an instrument ────────

        app.MapGet("/api/rankings/{instrument}/{accountId}/history", (
            string instrument,
            string accountId,
            int? days,
            GlobalLeaderboardPersistence persistence) =>
        {
            var db = persistence.GetOrCreateInstrumentDb(instrument);
            var history = db.GetRankHistory(accountId, days ?? 30);
            return Results.Ok(new { instrument, accountId, history });
        })
        .WithTags("Rankings")
        .RequireRateLimiting("public");

        // ─── Composite rankings (paginated) ────────────────────

        app.MapGet("/api/rankings/composite", (
            HttpContext httpContext,
            int? page,
            int? pageSize,
            MetaDatabase metaDb) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=1800, stale-while-revalidate=3600";
            var (entries, total) = metaDb.GetCompositeRankings(
                page ?? 1,
                Math.Clamp(pageSize ?? 50, 1, 200));

            var names = metaDb.GetDisplayNames(entries.Select(e => e.AccountId));

            var enriched = entries.Select(e => new
            {
                e.AccountId,
                displayName = names.GetValueOrDefault(e.AccountId),
                e.InstrumentsPlayed,
                e.TotalSongsPlayed,
                e.CompositeRating,
                e.CompositeRank,
                instruments = new
                {
                    guitar = e.GuitarAdjustedSkill.HasValue ? new { skill = e.GuitarAdjustedSkill, rank = e.GuitarSkillRank } : null,
                    bass = e.BassAdjustedSkill.HasValue ? new { skill = e.BassAdjustedSkill, rank = e.BassSkillRank } : null,
                    drums = e.DrumsAdjustedSkill.HasValue ? new { skill = e.DrumsAdjustedSkill, rank = e.DrumsSkillRank } : null,
                    vocals = e.VocalsAdjustedSkill.HasValue ? new { skill = e.VocalsAdjustedSkill, rank = e.VocalsSkillRank } : null,
                    proGuitar = e.ProGuitarAdjustedSkill.HasValue ? new { skill = e.ProGuitarAdjustedSkill, rank = e.ProGuitarSkillRank } : null,
                    proBass = e.ProBassAdjustedSkill.HasValue ? new { skill = e.ProBassAdjustedSkill, rank = e.ProBassSkillRank } : null,
                },
                e.ComputedAt,
            }).ToList();

            return Results.Ok(new
            {
                page = page ?? 1,
                pageSize = Math.Clamp(pageSize ?? 50, 1, 200),
                totalAccounts = total,
                entries = enriched,
            });
        })
        .WithTags("Rankings")
        .RequireRateLimiting("public");

        // ─── Single account composite ranking ──────────────────

        app.MapGet("/api/rankings/composite/{accountId}", (
            string accountId,
            MetaDatabase metaDb) =>
        {
            var ranking = metaDb.GetCompositeRanking(accountId);
            if (ranking is null)
                return Results.NotFound(new { error = "Account not found in composite rankings." });

            return Results.Ok(new
            {
                ranking.AccountId,
                displayName = metaDb.GetDisplayName(accountId),
                ranking.InstrumentsPlayed,
                ranking.TotalSongsPlayed,
                ranking.CompositeRating,
                ranking.CompositeRank,
                instruments = new
                {
                    guitar = ranking.GuitarAdjustedSkill.HasValue ? new { skill = ranking.GuitarAdjustedSkill, rank = ranking.GuitarSkillRank } : null,
                    bass = ranking.BassAdjustedSkill.HasValue ? new { skill = ranking.BassAdjustedSkill, rank = ranking.BassSkillRank } : null,
                    drums = ranking.DrumsAdjustedSkill.HasValue ? new { skill = ranking.DrumsAdjustedSkill, rank = ranking.DrumsSkillRank } : null,
                    vocals = ranking.VocalsAdjustedSkill.HasValue ? new { skill = ranking.VocalsAdjustedSkill, rank = ranking.VocalsSkillRank } : null,
                    proGuitar = ranking.ProGuitarAdjustedSkill.HasValue ? new { skill = ranking.ProGuitarAdjustedSkill, rank = ranking.ProGuitarSkillRank } : null,
                    proBass = ranking.ProBassAdjustedSkill.HasValue ? new { skill = ranking.ProBassAdjustedSkill, rank = ranking.ProBassSkillRank } : null,
                },
                ranking.ComputedAt,
            });
        })
        .WithTags("Rankings")
        .RequireRateLimiting("public");

        // ─── Combo leaderboard (paginated) ─────────────────────

        app.MapGet("/api/rankings/combo", (
            string? combo,
            string? instruments,
            string? rankBy,
            int? page,
            int? pageSize,
            MetaDatabase metaDb) =>
        {
            var comboId = ComboIds.NormalizeComboParam(combo ?? instruments);
            if (string.IsNullOrEmpty(comboId))
                return Results.BadRequest(new { error = "At least two instruments required. Use 'combo' (hex ID) or 'instruments' (e.g. Solo_Guitar+Solo_Bass)." });

            var metric = rankBy ?? "adjusted";
            var (entries, totalAccounts) = metaDb.GetComboLeaderboard(
                comboId, metric, page ?? 1, Math.Clamp(pageSize ?? 50, 1, 200));

            var enriched = entries.Select(e => new
            {
                e.Rank,
                e.AccountId,
                displayName = metaDb.GetDisplayName(e.AccountId),
                e.AdjustedRating,
                e.WeightedRating,
                e.FcRate,
                e.TotalScore,
                e.MaxScorePercent,
                e.SongsPlayed,
                e.FullComboCount,
                e.ComputedAt,
            }).ToList();

            return Results.Ok(new
            {
                comboId,
                rankBy = metric,
                page = page ?? 1,
                pageSize = Math.Clamp(pageSize ?? 50, 1, 200),
                totalAccounts,
                entries = enriched,
            });
        })
        .WithTags("Rankings")
        .RequireRateLimiting("public");

        // ─── Single account combo rank ─────────────────────────

        app.MapGet("/api/rankings/combo/{accountId}", (
            string accountId,
            string? combo,
            string? instruments,
            string? rankBy,
            MetaDatabase metaDb) =>
        {
            var comboId = ComboIds.NormalizeComboParam(combo ?? instruments);
            if (string.IsNullOrEmpty(comboId))
                return Results.BadRequest(new { error = "At least two instruments required. Use 'combo' (hex ID) or 'instruments' (e.g. Solo_Guitar+Solo_Bass)." });

            var metric = rankBy ?? "adjusted";
            var entry = metaDb.GetComboRank(comboId, accountId, metric);
            if (entry is null)
                return Results.NotFound(new { error = "Account not found in this combo ranking." });

            var totalAccounts = metaDb.GetComboTotalAccounts(comboId);

            return Results.Ok(new
            {
                comboId,
                rankBy = metric,
                entry.Rank,
                entry.AccountId,
                displayName = metaDb.GetDisplayName(accountId),
                entry.AdjustedRating,
                entry.WeightedRating,
                entry.FcRate,
                entry.TotalScore,
                entry.MaxScorePercent,
                entry.SongsPlayed,
                entry.FullComboCount,
                totalAccounts,
                entry.ComputedAt,
            });
        })
        .WithTags("Rankings")
        .RequireRateLimiting("public");
    }
}
