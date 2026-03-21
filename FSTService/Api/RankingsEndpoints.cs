using FSTService.Persistence;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    public static void MapRankingsEndpoints(this WebApplication app)
    {
        // ─── Per-instrument rankings (paginated) ───────────────

        app.MapGet("/api/rankings/{instrument}", (
            string instrument,
            string? rankBy,
            int? page,
            int? pageSize,
            GlobalLeaderboardPersistence persistence,
            MetaDatabase metaDb) =>
        {
            var db = persistence.GetOrCreateInstrumentDb(instrument);
            var (entries, total) = db.GetAccountRankings(
                rankBy ?? "adjusted",
                page ?? 1,
                Math.Clamp(pageSize ?? 50, 1, 200));

            // Resolve display names
            foreach (var entry in entries)
            {
                var name = metaDb.GetDisplayName(entry.AccountId);
                if (name is not null)
                {
                    // DTOs are init-only, create new with display name
                    // Actually AccountRankingDto has init setter, we need to project
                }
            }

            var enriched = entries.Select(e => new
            {
                e.AccountId,
                displayName = metaDb.GetDisplayName(e.AccountId),
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
            int? page,
            int? pageSize,
            MetaDatabase metaDb) =>
        {
            var (entries, total) = metaDb.GetCompositeRankings(
                page ?? 1,
                Math.Clamp(pageSize ?? 50, 1, 200));

            var enriched = entries.Select(e => new
            {
                e.AccountId,
                displayName = metaDb.GetDisplayName(e.AccountId),
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

            var combos = metaDb.GetUserComboRankings(accountId);

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
                comboRankings = combos.Select(c => new
                {
                    c.InstrumentCombo,
                    c.ComboRating,
                    c.ComboRank,
                    c.TotalAccountsInCombo,
                    c.ComputedAt,
                }),
            });
        })
        .WithTags("Rankings")
        .RequireRateLimiting("public");

        // ─── Set instrument preferences ────────────────────────

        app.MapPost("/api/player/{accountId}/instruments", (
            string accountId,
            InstrumentPrefsRequest body,
            MetaDatabase metaDb) =>
        {
            if (body.Instruments is null || body.Instruments.Count == 0)
                return Results.BadRequest(new { error = "At least one instrument must be specified." });

            metaDb.SetUserInstrumentPrefs(accountId, body.Instruments);
            return Results.Ok(new { accountId, instruments = body.Instruments });
        })
        .WithTags("Rankings")
        .RequireRateLimiting("protected")
        .RequireAuthorization();
    }
}

/// <summary>Request body for POST /api/player/{accountId}/instruments.</summary>
public sealed class InstrumentPrefsRequest
{
    public List<string> Instruments { get; set; } = new();
}
