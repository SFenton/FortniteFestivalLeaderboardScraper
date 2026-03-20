using FSTService.Persistence;
using FSTService.Scraping;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    public static void MapLeaderboardEndpoints(this WebApplication app)
    {
        app.MapGet("/api/leaderboard/{songId}/{instrument}", (
            string songId,
            string instrument,
            int? top,
            int? offset,
            double? leeway,
            GlobalLeaderboardPersistence persistence,
            MetaDatabase metaDb,
            PathDataStore pathStore) =>
        {
            int? maxScore = null;
            if (leeway.HasValue)
            {
                var allMax = pathStore.GetAllMaxScores();
                if (allMax.TryGetValue(songId, out var ms))
                {
                    var raw = ms.GetByInstrument(instrument);
                    if (raw.HasValue)
                        maxScore = (int)(raw.Value * (1.0 + leeway.Value / 100.0));
                }
            }
            var result = persistence.GetLeaderboardWithCount(songId, instrument, top, offset ?? 0, maxScore);
            if (result is null)
                return Results.NotFound(new { error = $"Unknown instrument: {instrument}" });

            var (entries, dbCount) = result.Value;
            var pop = metaDb.GetLeaderboardPopulation(songId, instrument);
            var totalEntries = Math.Max(pop > 0 ? (int)pop : 0, dbCount);
            var names = metaDb.GetDisplayNames(entries.Select(e => e.AccountId));
            var enriched = entries.Select(e => new
            {
                e.AccountId,
                DisplayName = names.GetValueOrDefault(e.AccountId),
                e.Score,
                e.Rank,
                e.Accuracy,
                e.IsFullCombo,
                e.Stars,
                e.Season,
                e.Percentile,
                e.EndTime,
            }).ToList();

            return Results.Ok(new
            {
                songId,
                instrument,
                count = enriched.Count,
                totalEntries,
                localEntries = dbCount,
                entries = enriched
            });
        })
        .WithTags("Leaderboards")
        .RequireRateLimiting("public");

        app.MapGet("/api/leaderboard/{songId}/all", (
            string songId,
            int? top,
            double? leeway,
            GlobalLeaderboardPersistence persistence,
            MetaDatabase metaDb,
            PathDataStore pathStore) =>
        {
            var instrumentKeys = persistence.GetInstrumentKeys();
            var population = metaDb.GetAllLeaderboardPopulation();
            var allAccountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Dictionary<string, SongMaxScores>? maxScoresMap = leeway.HasValue
                ? pathStore.GetAllMaxScores()
                : null;

            // Collect raw data per instrument
            var rawInstruments = new List<(string Instrument, List<LeaderboardEntryDto> Entries, int DbCount, int TotalEntries)>();
            foreach (var instrument in instrumentKeys)
            {
                int? maxScore = null;
                if (maxScoresMap is not null && maxScoresMap.TryGetValue(songId, out var ms))
                {
                    var raw = ms.GetByInstrument(instrument);
                    if (raw.HasValue)
                        maxScore = (int)(raw.Value * (1.0 + leeway!.Value / 100.0));
                }
                var result = persistence.GetLeaderboardWithCount(songId, instrument, top ?? 10, maxScore: maxScore);
                if (result is null) continue;

                var (entries, dbCount) = result.Value;
                var popKey = (songId, instrument);
                var totalEntries = Math.Max(
                    population.TryGetValue(popKey, out var pop) && pop > 0 ? (int)pop : 0,
                    dbCount);

                foreach (var e in entries)
                    allAccountIds.Add(e.AccountId);

                rawInstruments.Add((instrument, entries, dbCount, totalEntries));
            }

            // Single bulk name lookup
            var names = metaDb.GetDisplayNames(allAccountIds);

            var instruments = rawInstruments.Select(ri => new
            {
                instrument = ri.Instrument,
                count = ri.Entries.Count,
                totalEntries = ri.TotalEntries,
                localEntries = ri.DbCount,
                entries = ri.Entries.Select(e => new
                {
                    e.AccountId,
                    DisplayName = names.GetValueOrDefault(e.AccountId),
                    e.Score,
                    e.Rank,
                    e.Accuracy,
                    e.IsFullCombo,
                    e.Stars,
                    e.Season,
                    e.Percentile,
                    e.EndTime,
                }).ToList(),
            }).ToList();

            return Results.Ok(new
            {
                songId,
                instruments,
            });
        })
        .WithTags("Leaderboards")
        .RequireRateLimiting("public");
    }
}
