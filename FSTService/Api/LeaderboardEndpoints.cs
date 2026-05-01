using System.Text.Json;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Options;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    public static void MapLeaderboardEndpoints(this WebApplication app)
    {
        app.MapGet("/api/leaderboard/{songId}/bands/all", (
            HttpContext httpContext,
            string songId,
            int? top,
            string? accountId,
            string? selectedBandType,
            string? selectedTeamKey,
            string? combo,
            IMetaDatabase metaDb,
            ScrapeTimePrecomputer precomputer,
            [FromKeyedServices("LeaderboardAllCache")] ResponseCacheService lbCache) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=300, stale-while-revalidate=600";

            var effectiveTop = Math.Clamp(top ?? 10, 1, 50);
            var selectedAccountId = string.IsNullOrWhiteSpace(accountId) ? null : accountId.Trim();
            var normalizedSelectedBandType = string.IsNullOrWhiteSpace(selectedBandType) ? null : selectedBandType.Trim();
            var normalizedSelectedTeamKey = string.IsNullOrWhiteSpace(selectedTeamKey) ? null : selectedTeamKey.Trim();
            string? normalizedComboId = null;
            if (!string.IsNullOrWhiteSpace(combo))
            {
                if (normalizedSelectedBandType is null)
                    return Results.BadRequest(new { error = "selectedBandType is required when combo is supplied." });
                if (!BandComboIds.IsValidBandType(normalizedSelectedBandType))
                    return Results.NotFound(new { error = $"Unknown band type: {normalizedSelectedBandType}" });

                var comboValidation = BandComboIds.TryNormalizeForBandType(normalizedSelectedBandType, combo);
                if (comboValidation.Error is not null)
                    return Results.BadRequest(new { error = comboValidation.Error });
                normalizedComboId = comboValidation.ComboId;
            }
            var canUseGenericCache = effectiveTop == LeaderboardCacheKeys.SongDetailPreviewTop
                && selectedAccountId is null
                && normalizedSelectedTeamKey is null
                && normalizedComboId is null;

            if (canUseGenericCache)
            {
                var cacheKey = LeaderboardCacheKeys.SongBandLeaderboardsAll(songId, effectiveTop);
                var cachedResult = CacheHelper.ServeIfCached(httpContext, lbCache.Get(cacheKey));
                if (cachedResult is not null) return cachedResult;

                var precomputed = precomputer.TryGet(cacheKey);
                if (precomputed is not null)
                {
                    lbCache.Set(cacheKey, precomputed.Value.Json);
                    var precomputedResult = CacheHelper.ServeIfCached(httpContext, precomputed);
                    if (precomputedResult is not null) return precomputedResult;
                }
            }

            var bands = BuildSongBandLeaderboardsPayload(songId, effectiveTop, selectedAccountId,
                normalizedSelectedBandType, normalizedSelectedTeamKey, normalizedComboId, metaDb);

            if (canUseGenericCache)
            {
                var cacheKey = LeaderboardCacheKeys.SongBandLeaderboardsAll(songId, effectiveTop);
                var jsonBytes = SerializeJsonPayload(httpContext, new { songId, bands });
                var etag = lbCache.Set(cacheKey, jsonBytes);
                httpContext.Response.Headers.ETag = etag;
                return Results.Bytes(jsonBytes, "application/json");
            }

            return Results.Ok(new { songId, bands });
        })
        .WithTags("Leaderboards")
        .RequireRateLimiting("public");

        app.MapGet("/api/leaderboard/{songId}/bands/{bandType}", (
            HttpContext httpContext,
            string songId,
            string bandType,
            int? top,
            int? offset,
            string? accountId,
            string? teamKey,
            string? combo,
            IMetaDatabase metaDb) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=300";

            if (!BandComboIds.IsValidBandType(bandType))
                return Results.NotFound(new { error = $"Unknown band type: {bandType}" });

            var comboValidation = BandComboIds.TryNormalizeForBandType(bandType, combo);
            if (comboValidation.Error is not null)
                return Results.BadRequest(new { error = comboValidation.Error });

            var effectiveTop = Math.Clamp(top ?? 25, 1, 100);
            var effectiveOffset = Math.Max(0, offset ?? 0);
            var (entries, totalEntries) = metaDb.GetSongBandLeaderboard(songId, bandType, effectiveTop, effectiveOffset, comboValidation.ComboId);
            var selectedAccountId = string.IsNullOrWhiteSpace(accountId) ? null : accountId.Trim();
            var selectedPlayerEntry = selectedAccountId is null
                ? null
                : metaDb.GetSongBandLeaderboardEntryForAccount(songId, bandType, selectedAccountId, comboValidation.ComboId);
            var selectedTeamKey = string.IsNullOrWhiteSpace(teamKey) ? null : teamKey.Trim();
            var selectedBandEntry = selectedTeamKey is null
                ? null
                : metaDb.GetSongBandLeaderboardEntryForTeam(songId, bandType, selectedTeamKey, comboValidation.ComboId);
            IEnumerable<SongBandLeaderboardEntryDto> entriesForNames = entries;
            if (selectedPlayerEntry is not null)
                entriesForNames = entriesForNames.Append(selectedPlayerEntry);
            if (selectedBandEntry is not null)
                entriesForNames = entriesForNames.Append(selectedBandEntry);
            var names = metaDb.GetDisplayNames(entriesForNames.SelectMany(entry => entry.Members.Select(member => member.AccountId)));

            return Results.Ok(new
            {
                songId,
                bandType,
                count = entries.Count,
                totalEntries,
                localEntries = totalEntries,
                entries = MapSongBandLeaderboardEntries(entries, names),
                selectedPlayerEntry = selectedPlayerEntry is null ? null : MapSongBandLeaderboardEntry(selectedPlayerEntry, names),
                selectedBandEntry = selectedBandEntry is null ? null : MapSongBandLeaderboardEntry(selectedBandEntry, names),
            });
        })
        .WithTags("Leaderboards")
        .RequireRateLimiting("public");

        app.MapGet("/api/leaderboard/{songId}/{instrument}", (
            HttpContext httpContext,
            string songId,
            string instrument,
            int? top,
            int? offset,
            double? leeway,
            GlobalLeaderboardPersistence persistence,
            IMetaDatabase metaDb,
            IPathDataStore pathStore) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=300";
            if (!GlobalLeaderboardPersistence.IsValidInstrument(instrument))
                return Results.NotFound(new { error = $"Unknown instrument: {instrument}" });
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
            var result = persistence.GetCurrentStateLeaderboardWithCount(songId, instrument, top, offset ?? 0, maxScore);
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
                Rank = e.ApiRank > 0 ? e.ApiRank : e.Rank,
                e.Accuracy,
                e.IsFullCombo,
                e.Stars,
                e.Difficulty,
                e.Season,
                e.Percentile,
                e.EndTime,
                e.Source,
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
            HttpContext httpContext,
            string songId,
            int? top,
            double? leeway,
            GlobalLeaderboardPersistence persistence,
            IMetaDatabase metaDb,
            IPathDataStore pathStore,
            ScrapeTimePrecomputer precomputer,
            [FromKeyedServices("LeaderboardAllCache")] ResponseCacheService lbCache) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=300, stale-while-revalidate=600";
            var effectiveTop = top ?? LeaderboardCacheKeys.SongDetailPreviewTop;

            // ── Check cache ──────────────────────────────────────
            var legacyCacheKey = LeaderboardCacheKeys.LeaderboardAll(songId, effectiveTop, leeway);
            {
                var result = CacheHelper.ServeIfCached(httpContext, lbCache.Get(legacyCacheKey));
                if (result is not null) return result;
            }

            if (effectiveTop == LeaderboardCacheKeys.SongDetailPreviewTop)
            {
                var precomputed = precomputer.TryGet(legacyCacheKey);
                if (precomputed is not null)
                {
                    lbCache.Set(legacyCacheKey, precomputed.Value.Json);
                    var result = CacheHelper.ServeIfCached(httpContext, precomputed);
                    if (result is not null) return result;
                }
            }

            // ── Build response ───────────────────────────────────
            var instrumentKeys = persistence.GetInstrumentKeys();
            var population = metaDb.GetAllLeaderboardPopulation();

            Dictionary<string, SongMaxScores>? maxScoresMap = leeway.HasValue
                ? pathStore.GetAllMaxScores()
                : null;

            // Collect raw data per instrument (parallel — each instrument is a separate SQLite DB)
            var instrumentArr = instrumentKeys.ToArray();
            var rawResults = new (string Instrument, List<LeaderboardEntryDto> Entries, int DbCount, int TotalEntries)?[instrumentArr.Length];
            Parallel.For(0, instrumentArr.Length, i =>
            {
                var instrument = instrumentArr[i];
                int? maxScore = null;
                if (maxScoresMap is not null && maxScoresMap.TryGetValue(songId, out var ms))
                {
                    var raw = ms.GetByInstrument(instrument);
                    if (raw.HasValue)
                        maxScore = (int)(raw.Value * (1.0 + leeway!.Value / 100.0));
                }
                var result = persistence.GetCurrentStateLeaderboardWithCount(songId, instrument, effectiveTop, maxScore: maxScore);
                if (result is null) return;

                var (entries, dbCount) = result.Value;
                var popKey = (songId, instrument);
                var totalEntries = Math.Max(
                    population.TryGetValue(popKey, out var pop) && pop > 0 ? (int)pop : 0,
                    dbCount);

                rawResults[i] = (instrument, entries, dbCount, totalEntries);
            });

            var allAccountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rawInstruments = new List<(string Instrument, List<LeaderboardEntryDto> Entries, int DbCount, int TotalEntries)>();
            foreach (var r in rawResults)
            {
                if (r is null) continue;
                var val = r.Value;
                foreach (var e in val.Entries)
                    allAccountIds.Add(e.AccountId);
                rawInstruments.Add(val);
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
                    Rank = e.ApiRank > 0 ? e.ApiRank : e.Rank,
                    e.Accuracy,
                    e.IsFullCombo,
                    e.Stars,
                    e.Difficulty,
                    e.Season,
                    e.Percentile,
                    e.EndTime,
                }).ToList(),
            }).ToList();

            var payload = new
            {
                songId,
                instruments,
            };
            var jsonBytes = SerializeJsonPayload(httpContext, payload);
            var etag = lbCache.Set(legacyCacheKey, jsonBytes);

            httpContext.Response.Headers.ETag = etag;
            return Results.Bytes(jsonBytes, "application/json");
        })
        .WithTags("Leaderboards")
        .RequireRateLimiting("public");
    }

    private static List<object> BuildSongBandLeaderboardsPayload(
        string songId,
        int effectiveTop,
        string? selectedAccountId,
        string? normalizedSelectedBandType,
        string? normalizedSelectedTeamKey,
        string? normalizedComboId,
        IMetaDatabase metaDb) =>
        BandInstrumentMapping.AllBandTypes.Select(bandType =>
        {
            var bandComboId = normalizedSelectedBandType == bandType ? normalizedComboId : null;
            var (entries, totalEntries) = metaDb.GetSongBandLeaderboard(songId, bandType, effectiveTop, 0, bandComboId);
            var selectedPlayerEntry = selectedAccountId is null
                ? null
                : metaDb.GetSongBandLeaderboardEntryForAccount(songId, bandType, selectedAccountId, bandComboId);
            var selectedBandEntry = normalizedSelectedBandType == bandType && normalizedSelectedTeamKey is not null
                ? metaDb.GetSongBandLeaderboardEntryForTeam(songId, bandType, normalizedSelectedTeamKey, bandComboId)
                : null;
            IEnumerable<SongBandLeaderboardEntryDto> entriesForNames = entries;
            if (selectedPlayerEntry is not null)
                entriesForNames = entriesForNames.Append(selectedPlayerEntry);
            if (selectedBandEntry is not null)
                entriesForNames = entriesForNames.Append(selectedBandEntry);
            var names = metaDb.GetDisplayNames(entriesForNames.SelectMany(entry => entry.Members.Select(member => member.AccountId)));
            return new
            {
                bandType,
                count = entries.Count,
                totalEntries,
                localEntries = totalEntries,
                entries = MapSongBandLeaderboardEntries(entries, names),
                selectedPlayerEntry = selectedPlayerEntry is null ? null : MapSongBandLeaderboardEntry(selectedPlayerEntry, names),
                selectedBandEntry = selectedBandEntry is null ? null : MapSongBandLeaderboardEntry(selectedBandEntry, names),
            };
        }).Cast<object>().ToList();

    private static byte[] SerializeJsonPayload(HttpContext httpContext, object payload)
    {
        var jsonOpts = httpContext.RequestServices
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
            .Value.SerializerOptions;
        return JsonSerializer.SerializeToUtf8Bytes(payload, jsonOpts);
    }

    private static List<object> MapSongBandLeaderboardEntries(
        IEnumerable<SongBandLeaderboardEntryDto> entries,
        IReadOnlyDictionary<string, string> names) =>
        entries.Select(entry => MapSongBandLeaderboardEntry(entry, names)).ToList();

    private static object MapSongBandLeaderboardEntry(
        SongBandLeaderboardEntryDto entry,
        IReadOnlyDictionary<string, string> names) => new
        {
            entry.BandId,
            entry.BandType,
            entry.TeamKey,
            entry.ComboId,
            Members = entry.Members.Select(member => new
            {
                member.AccountId,
                DisplayName = names.GetValueOrDefault(member.AccountId),
                member.Instruments,
                member.Score,
                member.Accuracy,
                member.IsFullCombo,
                member.Stars,
                member.Difficulty,
                member.Season,
            }).ToList(),
            entry.Score,
            entry.Rank,
            entry.Accuracy,
            entry.IsFullCombo,
            entry.Stars,
            entry.Difficulty,
            entry.Season,
            entry.Percentile,
            entry.EndTime,
        };
}
