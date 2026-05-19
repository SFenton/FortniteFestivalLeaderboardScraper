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

            IResult? serveGenericCachedPreview()
            {
                var cacheKey = LeaderboardCacheKeys.SongBandLeaderboardsAll(songId, effectiveTop);
                var cachedResult = CacheHelper.ServeIfCached(httpContext, lbCache.Get(cacheKey));
                if (cachedResult is not null) return cachedResult;

                var precomputed = precomputer.TryGet(cacheKey);
                if (precomputed is null) return null;

                lbCache.Set(cacheKey, precomputed.Value.Json);
                return CacheHelper.ServeIfCached(httpContext, precomputed);
            }

            if (canUseGenericCache)
            {
                var cachedResult = serveGenericCachedPreview();
                if (cachedResult is not null) return cachedResult;

                var frozenMiss = CacheHelper.ServeUnavailableIfFrozen(httpContext, lbCache);
                if (frozenMiss is not null) return frozenMiss;
            }
            else
            {
                if (lbCache.RequiresCachedReads && lbCache.IsFrozen && normalizedComboId is null)
                {
                    var cachedResult = serveGenericCachedPreview();
                    if (cachedResult is not null) return cachedResult;
                }

                if (lbCache.IsFrozen)
                {
                    var frozenMiss = CacheHelper.ServeUnavailableIfFrozen(httpContext, lbCache);
                    if (frozenMiss is not null) return frozenMiss;
                }
            }

            var showLeaderboardEntryTotals = metaDb.ShouldShowLeaderboardEntryTotals();
            var bands = BuildSongBandLeaderboardsPayload(songId, effectiveTop, selectedAccountId,
                normalizedSelectedBandType, normalizedSelectedTeamKey, normalizedComboId, metaDb);

            if (canUseGenericCache)
            {
                var cacheKey = LeaderboardCacheKeys.SongBandLeaderboardsAll(songId, effectiveTop);
                var jsonBytes = SerializeJsonPayload(httpContext, new { songId, showLeaderboardEntryTotals, bands });
                var etag = lbCache.Set(cacheKey, jsonBytes);
                httpContext.Response.Headers.ETag = etag;
                return Results.Bytes(jsonBytes, "application/json");
            }

            return Results.Ok(new { songId, showLeaderboardEntryTotals, bands });
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
            IMetaDatabase metaDb,
            [FromKeyedServices("LeaderboardAllCache")] ResponseCacheService lbCache) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=300";

            if (!BandComboIds.IsValidBandType(bandType))
                return Results.NotFound(new { error = $"Unknown band type: {bandType}" });

            var comboValidation = BandComboIds.TryNormalizeForBandType(bandType, combo);
            if (comboValidation.Error is not null)
                return Results.BadRequest(new { error = comboValidation.Error });

            var effectiveTop = Math.Clamp(top ?? 25, 1, 100);
            var effectiveOffset = Math.Max(0, offset ?? 0);
            var frozenMiss = CacheHelper.ServeUnavailableIfFrozen(httpContext, lbCache);
            if (frozenMiss is not null) return frozenMiss;

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
            var showLeaderboardEntryTotals = metaDb.ShouldShowLeaderboardEntryTotals();

            return Results.Ok(new
            {
                songId,
                bandType,
                showLeaderboardEntryTotals,
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

        app.MapGet("/api/leaderboard/{songId}/members/scores", (
            HttpContext httpContext,
            string songId,
            string? accountIds,
            string? instruments,
            double? leeway,
            GlobalLeaderboardPersistence persistence,
            IMetaDatabase metaDb,
            IPathDataStore pathStore,
            [FromKeyedServices("LeaderboardAllCache")] ResponseCacheService lbCache) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=300";

            var selectedAccountIds = ParseCsvParameter(accountIds, maxItems: 8);
            if (selectedAccountIds.Count == 0)
                return Results.BadRequest(new { error = "accountIds is required." });

            HashSet<string>? instrumentFilter = null;
            var requestedInstruments = ParseCsvParameter(instruments, maxItems: 16);
            if (requestedInstruments.Count > 0)
            {
                foreach (var instrument in requestedInstruments)
                {
                    if (!GlobalLeaderboardPersistence.IsValidInstrument(instrument))
                        return Results.NotFound(new { error = $"Unknown instrument: {instrument}" });
                }
                instrumentFilter = new HashSet<string>(requestedInstruments, StringComparer.OrdinalIgnoreCase);
            }

            var frozenMiss = CacheHelper.ServeUnavailableIfFrozen(httpContext, lbCache);
            if (frozenMiss is not null) return frozenMiss;

            var profilesByAccount = persistence.GetCurrentStatePlayerProfiles(selectedAccountIds, songId, instrumentFilter);
            var scoreRows = selectedAccountIds
                .SelectMany(accountId => profilesByAccount.TryGetValue(accountId, out var scores)
                    ? scores.Select(score => (AccountId: accountId, Score: score))
                    : [])
                .ToList();

            Dictionary<string, Dictionary<string, int>>? maxScoresByInstrument = null;
            Dictionary<(string SongId, string Instrument), int>? flatThresholds = null;
            if (leeway.HasValue && scoreRows.Count > 0)
            {
                var allMax = pathStore.GetAllMaxScores();
                if (allMax.Count > 0)
                {
                    maxScoresByInstrument = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
                    flatThresholds = new Dictionary<(string, string), int>();
                    foreach (var (_, score) in scoreRows)
                    {
                        if (!allMax.TryGetValue(score.SongId, out var songMax)) continue;
                        var raw = songMax.GetByInstrument(score.Instrument);
                        if (!raw.HasValue) continue;
                        var threshold = (int)(raw.Value * (1.0 + leeway.Value / 100.0));
                        if (!maxScoresByInstrument.TryGetValue(score.Instrument, out var instDict))
                        {
                            instDict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                            maxScoresByInstrument[score.Instrument] = instDict;
                        }

                        instDict[score.SongId] = threshold;
                        flatThresholds[(score.SongId, score.Instrument)] = threshold;
                    }
                }
            }

            var rankingsByAccount = new Dictionary<string, Dictionary<(string SongId, string Instrument), int>>(StringComparer.OrdinalIgnoreCase);
            var validFallbacksByAccount = new Dictionary<string, Dictionary<(string SongId, string Instrument), ValidScoreFallback>>(StringComparer.OrdinalIgnoreCase);
            if (maxScoresByInstrument is not null)
            {
                foreach (var accountId in selectedAccountIds)
                {
                    rankingsByAccount[accountId] = persistence.GetCurrentStatePlayerRankingsFiltered(accountId, maxScoresByInstrument, songId, instrumentFilter);
                    if (flatThresholds is null) continue;

                    var invalidThresholds = new Dictionary<(string, string), int>();
                    foreach (var (rowAccountId, score) in scoreRows)
                    {
                        if (!string.Equals(rowAccountId, accountId, StringComparison.OrdinalIgnoreCase)) continue;
                        var key = (score.SongId, score.Instrument);
                        if (flatThresholds.TryGetValue(key, out var threshold) && score.Score > threshold)
                            invalidThresholds[key] = threshold;
                    }

                    if (invalidThresholds.Count > 0)
                        validFallbacksByAccount[accountId] = metaDb.GetBestValidScores(accountId, invalidThresholds);
                }
            }

            var filteredPopulation = maxScoresByInstrument is not null
                ? persistence.GetCurrentStateFilteredPopulation(maxScoresByInstrument, instrumentFilter)
                : null;
            var unfilteredPopulation = metaDb.GetAllLeaderboardPopulation();
            var names = metaDb.GetDisplayNames(selectedAccountIds);

            var responseScores = scoreRows.Select(row =>
            {
                var accountId = row.AccountId;
                var score = row.Score;
                var key = (score.SongId, score.Instrument);
                var computedRank = rankingsByAccount.TryGetValue(accountId, out var accountRankings)
                    ? accountRankings.GetValueOrDefault(key, 0)
                    : 0;
                var rank = LeaderboardResponseRanks.Resolve(score.ApiRank, computedRank, score.Rank, maxScoresByInstrument is not null);
                var totalEntries = unfilteredPopulation.TryGetValue(key, out var pop) && pop > 0 ? (int)pop : 0;

                bool? isValid = null;
                int? validScore = null;
                int? validAccuracy = null;
                bool? validIsFullCombo = null;
                int? validStars = null;
                int? validRank = null;
                int? validTotalEntries = null;

                if (flatThresholds is not null)
                {
                    var hasThreshold = flatThresholds.TryGetValue(key, out var threshold);
                    var scoreIsValid = !hasThreshold || score.Score <= threshold;
                    isValid = scoreIsValid;

                    if (scoreIsValid)
                    {
                        validScore = score.Score;
                        validAccuracy = score.Accuracy;
                        validIsFullCombo = score.IsFullCombo;
                        validStars = score.Stars;
                        validRank = computedRank > 0 ? computedRank : rank;
                    }
                    else if (validFallbacksByAccount.TryGetValue(accountId, out var fallbacks) && fallbacks.TryGetValue(key, out var fallback))
                    {
                        validScore = fallback.Score;
                        validAccuracy = fallback.Accuracy;
                        validIsFullCombo = fallback.IsFullCombo;
                        validStars = fallback.Stars;
                        validRank = persistence.GetCurrentStateRankForScore(score.Instrument, score.SongId, fallback.Score, threshold);
                    }

                    if (filteredPopulation is not null)
                        validTotalEntries = filteredPopulation.GetValueOrDefault(key, totalEntries);
                }

                return new
                {
                    accountId,
                    displayName = names.GetValueOrDefault(accountId),
                    songId = score.SongId,
                    instrument = score.Instrument,
                    score = score.Score,
                    rank,
                    localRank = computedRank > 0 ? computedRank : (int?)null,
                    percentile = score.Percentile,
                    accuracy = score.Accuracy,
                    isFullCombo = score.IsFullCombo,
                    stars = score.Stars,
                    season = score.Season,
                    difficulty = score.Difficulty,
                    endTime = score.EndTime,
                    totalEntries,
                    isValid,
                    validScore,
                    validAccuracy,
                    validIsFullCombo,
                    validStars,
                    validRank,
                    validTotalEntries,
                };
            }).ToList();

            return Results.Ok(new { songId, scores = responseScores });
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
            IPathDataStore pathStore,
            [FromKeyedServices("LeaderboardAllCache")] ResponseCacheService lbCache) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=300";
            if (!GlobalLeaderboardPersistence.IsValidInstrument(instrument))
                return Results.NotFound(new { error = $"Unknown instrument: {instrument}" });

            var frozenMiss = CacheHelper.ServeUnavailableIfFrozen(httpContext, lbCache);
            if (frozenMiss is not null) return frozenMiss;

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
            var useFilteredRank = maxScore.HasValue;
            var pop = metaDb.GetLeaderboardPopulation(songId, instrument);
            var totalEntries = Math.Max(pop > 0 ? (int)pop : 0, dbCount);
            var showLeaderboardEntryTotals = metaDb.ShouldShowLeaderboardEntryTotals();
            var names = metaDb.GetDisplayNames(entries.Select(e => e.AccountId));
            var enriched = entries.Select(e => new
            {
                e.AccountId,
                DisplayName = names.GetValueOrDefault(e.AccountId),
                e.Score,
                Rank = LeaderboardResponseRanks.Resolve(e.ApiRank, e.Rank, e.Rank, useFilteredRank),
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
                showLeaderboardEntryTotals,
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

            {
                var frozenMiss = CacheHelper.ServeUnavailableIfFrozen(httpContext, lbCache);
                if (frozenMiss is not null) return frozenMiss;
            }

            // ── Build response ───────────────────────────────────
            var instrumentKeys = persistence.GetInstrumentKeys();
            var population = metaDb.GetAllLeaderboardPopulation();
            var showLeaderboardEntryTotals = metaDb.ShouldShowLeaderboardEntryTotals();

            Dictionary<string, SongMaxScores>? maxScoresMap = leeway.HasValue
                ? pathStore.GetAllMaxScores()
                : null;

            // Collect raw data per instrument (parallel — each instrument is a separate SQLite DB)
            var instrumentArr = instrumentKeys.ToArray();
            var rawResults = new (string Instrument, List<LeaderboardEntryDto> Entries, int DbCount, int TotalEntries, bool UseFilteredRank)?[instrumentArr.Length];
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
                var useFilteredRank = maxScore.HasValue;
                var result = persistence.GetCurrentStateLeaderboardWithCount(songId, instrument, effectiveTop, maxScore: maxScore);
                if (result is null) return;

                var (entries, dbCount) = result.Value;
                var popKey = (songId, instrument);
                var totalEntries = Math.Max(
                    population.TryGetValue(popKey, out var pop) && pop > 0 ? (int)pop : 0,
                    dbCount);

                rawResults[i] = (instrument, entries, dbCount, totalEntries, useFilteredRank);
            });

            var allAccountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rawInstruments = new List<(string Instrument, List<LeaderboardEntryDto> Entries, int DbCount, int TotalEntries, bool UseFilteredRank)>();
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
                    Rank = LeaderboardResponseRanks.Resolve(e.ApiRank, e.Rank, e.Rank, ri.UseFilteredRank),
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
                showLeaderboardEntryTotals,
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

    private static List<string> ParseCsvParameter(string? value, int maxItems)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, maxItems))
            .ToList();
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
