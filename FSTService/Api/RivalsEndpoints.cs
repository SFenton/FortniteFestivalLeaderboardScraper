using FortniteFestival.Core.Models;
using FortniteFestival.Core.Services;
using FSTService.Persistence;
using FSTService.Scraping;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    public static void MapRivalsEndpoints(this WebApplication app)
    {
        // ─── Combo overview ────────────────────────────────────────

        app.MapGet("/api/player/{accountId}/rivals", (
            string accountId,
            MetaDatabase metaDb) =>
        {
            var status = metaDb.GetRivalsStatus(accountId);
            var combos = metaDb.GetRivalCombos(accountId);

            return Results.Ok(new
            {
                accountId,
                computedAt = status?.CompletedAt,
                combos = combos.Select(c => new
                {
                    combo = c.InstrumentCombo,
                    aboveCount = c.AboveCount,
                    belowCount = c.BelowCount,
                }).ToList(),
            });
        })
        .WithTags("Rivals")
        .RequireRateLimiting("public");

        // ─── Rival list for a specific combo ───────────────────────

        app.MapGet("/api/player/{accountId}/rivals/{combo}", (
            string accountId,
            string combo,
            MetaDatabase metaDb) =>
        {
            var above = metaDb.GetUserRivals(accountId, combo, "above");
            var below = metaDb.GetUserRivals(accountId, combo, "below");

            if (above.Count == 0 && below.Count == 0)
                return Results.NotFound(new { error = "No rivals found for this combo." });

            var rivalIds = above.Concat(below).Select(r => r.RivalAccountId).Distinct().ToList();
            var names = rivalIds.ToDictionary(
                id => id,
                id => metaDb.GetDisplayName(id),
                StringComparer.OrdinalIgnoreCase);

            return Results.Ok(new
            {
                combo,
                above = above.Select(r => MapRivalSummary(r, names)),
                below = below.Select(r => MapRivalSummary(r, names)),
            });
        })
        .WithTags("Rivals")
        .RequireRateLimiting("public");

        // ─── Detailed comparison with a rival for a combo (paginated) ──

        app.MapGet("/api/player/{accountId}/rivals/{combo}/{rivalId}", (
            string accountId,
            string combo,
            string rivalId,
            int? limit,
            int? offset,
            string? sort,
            MetaDatabase metaDb,
            FestivalService festivalService,
            RivalsCalculator rivalsCalculator) =>
        {
            // Parse combo into instruments — accepts hex ID ("03") or legacy ("Solo_Guitar+Solo_Bass") or single ("Solo_Guitar")
            string[] instruments;
            if (combo.Contains('+'))
                instruments = combo.Split('+');
            else if (combo.Length <= 2 && int.TryParse(combo, System.Globalization.NumberStyles.HexNumber, null, out _))
                instruments = ComboIds.ToInstruments(combo).ToArray();
            else
                instruments = [combo]; // single instrument name
            var allSamples = new List<RivalSongSampleRow>();
            foreach (var inst in instruments)
            {
                allSamples.AddRange(metaDb.GetRivalSongSamples(accountId, rivalId, inst));
            }

            if (allSamples.Count == 0)
                return Results.NotFound(new { error = "No song data for this rival." });

            // Sort
            var sortMode = sort?.ToLowerInvariant() ?? "closest";
            IEnumerable<RivalSongSampleRow> sorted = sortMode switch
            {
                "they_lead" => allSamples.OrderBy(s => s.RankDelta),
                "you_lead" => allSamples.OrderByDescending(s => s.RankDelta),
                _ => allSamples.OrderBy(s => Math.Abs(s.RankDelta)),
            };

            var total = allSamples.Count;
            var effectiveLimit = limit ?? 50;
            var effectiveOffset = offset ?? 0;

            // limit=0 means all
            var page = effectiveLimit == 0
                ? sorted.Skip(effectiveOffset).ToList()
                : sorted.Skip(effectiveOffset).Take(effectiveLimit).ToList();

            var songLookup = festivalService.Songs
                .Where(s => s.track?.su is not null)
                .ToDictionary(s => s.track.su, StringComparer.OrdinalIgnoreCase);
            var rivalName = metaDb.GetDisplayName(rivalId);

            // Compute song gaps on-the-fly
            var gaps = rivalsCalculator.ComputeSongGaps(accountId, rivalId, instruments);

            return Results.Ok(new
            {
                rival = new { accountId = rivalId, displayName = rivalName },
                combo,
                totalSongs = total,
                offset = effectiveOffset,
                limit = effectiveLimit,
                sort = sortMode,
                songs = page.Select(s =>
                {
                    songLookup.TryGetValue(s.SongId, out var song);
                    return new
                    {
                        s.SongId,
                        title = song?.track?.tt,
                        artist = song?.track?.an,
                        s.Instrument,
                        s.UserRank,
                        s.RivalRank,
                        s.RankDelta,
                        s.UserScore,
                        s.RivalScore,
                    };
                }).ToList(),
                songsToCompete = gaps.SongsToCompete.Select(g =>
                {
                    songLookup.TryGetValue(g.SongId, out var song);
                    return new
                    {
                        g.SongId,
                        title = song?.track?.tt,
                        artist = song?.track?.an,
                        g.Instrument,
                        g.Score,
                        g.Rank,
                    };
                }).ToList(),
                yourExclusiveSongs = gaps.YourExclusives.Select(g =>
                {
                    songLookup.TryGetValue(g.SongId, out var song);
                    return new
                    {
                        g.SongId,
                        title = song?.track?.tt,
                        artist = song?.track?.an,
                        g.Instrument,
                        g.Score,
                        g.Rank,
                    };
                }).ToList(),
            });
        })
        .WithTags("Rivals")
        .RequireRateLimiting("public");

        // ─── Per-instrument songs for a rival (no combo context) ───

        app.MapGet("/api/player/{accountId}/rivals/{rivalId}/songs/{instrument}", (
            string accountId,
            string rivalId,
            string instrument,
            int? limit,
            int? offset,
            string? sort,
            MetaDatabase metaDb,
            FestivalService festivalService) =>
        {
            var samples = metaDb.GetRivalSongSamples(accountId, rivalId, instrument);
            if (samples.Count == 0)
                return Results.NotFound(new { error = "No song data for this rival on this instrument." });

            var sortMode = sort?.ToLowerInvariant() ?? "closest";
            IEnumerable<RivalSongSampleRow> sorted = sortMode switch
            {
                "they_lead" => samples.OrderBy(s => s.RankDelta),
                "you_lead" => samples.OrderByDescending(s => s.RankDelta),
                _ => samples.OrderBy(s => Math.Abs(s.RankDelta)),
            };

            var total = samples.Count;
            var effectiveLimit = limit ?? 50;
            var effectiveOffset = offset ?? 0;
            var page = effectiveLimit == 0
                ? sorted.Skip(effectiveOffset).ToList()
                : sorted.Skip(effectiveOffset).Take(effectiveLimit).ToList();

            var songLookup = festivalService.Songs
                .Where(s => s.track?.su is not null)
                .ToDictionary(s => s.track.su, StringComparer.OrdinalIgnoreCase);
            var rivalName = metaDb.GetDisplayName(rivalId);

            return Results.Ok(new
            {
                rival = new { accountId = rivalId, displayName = rivalName },
                instrument,
                totalSongs = total,
                offset = effectiveOffset,
                limit = effectiveLimit,
                sort = sortMode,
                songs = page.Select(s =>
                {
                    songLookup.TryGetValue(s.SongId, out var song);
                    return new
                    {
                        s.SongId,
                        title = song?.track?.tt,
                        artist = song?.track?.an,
                        s.UserRank,
                        s.RivalRank,
                        s.RankDelta,
                        s.UserScore,
                        s.RivalScore,
                    };
                }).ToList(),
            });
        })
        .WithTags("Rivals")
        .RequireRateLimiting("public");

        // ─── Force recomputation ───────────────────────────────────

        app.MapPost("/api/player/{accountId}/rivals/recompute", (
            string accountId,
            MetaDatabase metaDb,
            RivalsOrchestrator rivalsOrchestrator) =>
        {
            metaDb.EnsureRivalsStatus(accountId);
            rivalsOrchestrator.ComputeForUser(accountId);
            return Results.Ok(new { accountId, status = "recomputed" });
        })
        .WithTags("Rivals")
        .RequireRateLimiting("protected")
        .RequireAuthorization();
    }

    private static object MapRivalSummary(UserRivalRow r, Dictionary<string, string?> names)
    {
        return new
        {
            accountId = r.RivalAccountId,
            displayName = names.GetValueOrDefault(r.RivalAccountId),
            rivalScore = r.RivalScore,
            sharedSongCount = r.SharedSongCount,
            aheadCount = r.AheadCount,
            behindCount = r.BehindCount,
            avgSignedDelta = r.AvgSignedDelta,
        };
    }
}
