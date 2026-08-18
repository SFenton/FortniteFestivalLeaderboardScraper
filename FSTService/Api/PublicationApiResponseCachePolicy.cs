using FSTService.Persistence;
using FSTService.Scraping;

namespace FSTService.Api;

public enum PublicApiCacheTransform
{
    None,
    FirstPageSubset,
    OverviewSubset,
}

public sealed record PublicApiCacheLookupCandidate(
    string CacheKey,
    PublicApiCacheTransform Transform =
        PublicApiCacheTransform.None,
    int Page = 1,
    int PageSize = 0);

public sealed record PublicApiCacheRequestPlan(
    string RequestCacheKey,
    IReadOnlyList<PublicApiCacheLookupCandidate>
        LookupCandidates,
    bool FreezeCritical,
    bool AllowWriteThrough,
    TimeSpan MaxBuildDuration,
    int MaxPayloadBytes,
    string? ResponseCacheControl = null);

public static class PublicationApiCacheKeys
{
    public const string Songs = "public-api:songs:v1";

    public static string InstrumentLeaderboard(
        string songId,
        string instrument,
        int top,
        double? leeway) =>
        string.Concat(
            "leaderboard:instrument:",
            songId,
            ":",
            instrument,
            ":",
            top.ToString(
                System.Globalization.CultureInfo
                    .InvariantCulture),
            ":",
            leeway?.ToString(
                "0.########",
                System.Globalization.CultureInfo.InvariantCulture)
            ?? string.Empty);
}

internal static class PublicApiResponseCachePolicy
{
    private static readonly string[] LivePrefixes =
    [
        "/api/account/",
        "/api/admin/",
        "/api/backfill/",
        "/api/diag/",
        "/api/paths/",
    ];

    private static readonly string[] LiveExactPaths =
    [
        "/api/progress",
        "/api/service-info",
        "/api/shop",
        "/api/status",
        "/api/version",
    ];

    private static readonly HashSet<string> RankingMetrics =
        new(
        [
            "adjusted",
            "weighted",
            "totalscore",
            "fcrate",
            "maxscore",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static bool TryCreateRequestPlan(
        HttpContext context,
        out PublicApiCacheRequestPlan plan)
    {
        plan = default!;
        if (!IsAuthoritativePublicationBound(context))
            return false;

        var request = context.Request;
        if (!IsCacheableRequest(request, out var cacheKey))
            return false;

        var candidates =
            new List<PublicApiCacheLookupCandidate>
            {
                new(cacheKey),
            };
        var freezeCritical = false;
        var allowWriteThrough = false;
        string? responseCacheControl = null;
        string? normalizedRequestCacheKey = null;
        var path = request.Path.Value!;
        var segments = path.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);

        if (string.Equals(
                path,
                "/api/songs",
                StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(new(PublicationApiCacheKeys.Songs));
            freezeCritical = true;
            allowWriteThrough = true;
            responseCacheControl =
                "public, max-age=1800, stale-while-revalidate=3600";
        }
        else if (TryPlanRankings(
                     request,
                     segments,
                     candidates,
                     out var lazy,
                     out normalizedRequestCacheKey))
        {
            freezeCritical = true;
            allowWriteThrough = lazy;
            responseCacheControl =
                "public, max-age=1800, stale-while-revalidate=3600";
        }
        else if (TryPlanPlayer(
                     request,
                     segments,
                     candidates))
        {
            freezeCritical = true;
            responseCacheControl =
                "public, max-age=120, stale-while-revalidate=300";
        }
        else if (TryPlanLeaderboard(
                     request,
                     segments,
                     candidates))
        {
            freezeCritical = true;
            var aggregateLeaderboard =
                (segments.Length == 4
                 && string.Equals(
                     segments[3],
                     "all",
                     StringComparison.OrdinalIgnoreCase))
                || (segments.Length == 5
                    && string.Equals(
                        segments[3],
                        "bands",
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        segments[4],
                        "all",
                        StringComparison.OrdinalIgnoreCase));
            responseCacheControl =
                aggregateLeaderboard
                    ? "public, max-age=300, stale-while-revalidate=600"
                    : "public, max-age=300";
        }

        plan = new PublicApiCacheRequestPlan(
            normalizedRequestCacheKey ?? cacheKey,
            candidates
                .DistinctBy(static candidate =>
                    (
                        candidate.CacheKey,
                        candidate.Transform,
                        candidate.Page,
                        candidate.PageSize))
                .ToArray(),
            freezeCritical,
            allowWriteThrough,
            TimeSpan.FromSeconds(1),
            2 * 1024 * 1024,
            responseCacheControl);
        return true;
    }

    internal static bool IsAuthoritativePublicationBound(
        HttpContext context)
    {
        var classifications = context.GetEndpoint()
            ?.Metadata
            .GetOrderedMetadata<
                ApiPublicationClassification>();
        return classifications is { Count: 1 }
               && classifications[0]
                   is PublicationBound;
    }

    public static bool IsCacheableRequest(
        HttpRequest request,
        out string cacheKey)
    {
        cacheKey = string.Empty;

        if (!HttpMethods.IsGet(request.Method)
            || request.HttpContext.WebSockets
                .IsWebSocketRequest)
        {
            return false;
        }

        var path = request.Path.Value;
        if (string.IsNullOrWhiteSpace(path)
            || !path.StartsWith(
                "/api/",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (LiveExactPaths.Any(livePath =>
                string.Equals(
                    path,
                    livePath,
                    StringComparison.OrdinalIgnoreCase))
            || LivePrefixes.Any(prefix =>
                path.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase))
            || HasSelectedOverlayQuery(request)
            || path.EndsWith(
                "/notifications",
                StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(
                "/diagnostics",
                StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(
                "/sync-status",
                StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(
                "/export",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        cacheKey = BuildCacheKey(request);
        return true;
    }

    internal static string BuildCacheKey(
        HttpRequest request)
    {
        var songs = string.Equals(
            request.Path.Value,
            "/api/songs",
            StringComparison.OrdinalIgnoreCase);
        var profileInvariant =
            songs
            || IsProfileInvariantRequest(request.Path);
        var selectedProfileType = profileInvariant
            ? string.Empty
            : HeaderValue(
                request,
                SelectedProfileHeaders
                    .SelectedProfileTypeHeader);
        var selectedProfileId = profileInvariant
            ? string.Empty
            : HeaderValue(
                request,
                SelectedProfileHeaders
                    .SelectedProfileIdHeader);
        var legacySelectedPlayer = profileInvariant
            ? string.Empty
            : HeaderValue(
                request,
                SelectedProfileHeaders
                    .LegacySelectedPlayerHeader);
        var selectedBandId = profileInvariant
            ? string.Empty
            : HeaderValue(
                request,
                SelectedProfileHeaders
                    .SelectedBandIdHeader);
        var selectedBandType = profileInvariant
            ? string.Empty
            : HeaderValue(
                request,
                SelectedProfileHeaders
                    .SelectedBandTypeHeader);
        var selectedBandTeamKey = profileInvariant
            ? string.Empty
            : HeaderValue(
                request,
                SelectedProfileHeaders
                    .SelectedBandTeamKeyHeader);
        var routeCacheVersion =
            request.Path.StartsWithSegments(
                new PathString("/api/leaderboard"),
                StringComparison.OrdinalIgnoreCase)
                ? "|routeVersion=rank-offsets-v1"
                : string.Empty;

        return string.Concat(
            "public-route:",
            request.Path.Value,
            songs
                ? string.Empty
                : BuildCanonicalQueryString(request),
            routeCacheVersion,
            "|profileType=",
            selectedProfileType,
            "|profileId=",
            selectedProfileId,
            "|legacyPlayer=",
            legacySelectedPlayer,
            "|bandId=",
            selectedBandId,
            "|bandType=",
            selectedBandType,
            "|teamKey=",
            selectedBandTeamKey);
    }

    internal static string BuildCacheKeyForRequestTarget(
        string requestTarget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            requestTarget);
        var queryIndex = requestTarget.IndexOf(
            '?',
            StringComparison.Ordinal);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = queryIndex < 0
            ? requestTarget
            : requestTarget[..queryIndex];
        if (queryIndex >= 0)
        {
            context.Request.QueryString =
                new QueryString(
                    requestTarget[queryIndex..]);
        }

        return BuildCacheKey(context.Request);
    }

    private static bool TryPlanRankings(
        HttpRequest request,
        string[] segments,
        List<PublicApiCacheLookupCandidate> candidates,
        out bool lazy,
        out string? normalizedRequestCacheKey)
    {
        lazy = false;
        normalizedRequestCacheKey = null;
        if (segments.Length < 3
            || !string.Equals(
                segments[0],
                "api",
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                segments[1],
                "rankings",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var metric = (QueryValue(
            request,
            "rankBy") ?? "adjusted")
            .ToLowerInvariant();
        if (!RankingMetrics.Contains(metric))
            return false;

        if (segments.Length == 3
            && string.Equals(
                segments[2],
                "overview",
                StringComparison.OrdinalIgnoreCase)
            && OnlyQueryKeys(
                request,
                "rankBy",
                "pageSize"))
        {
            if (!TryQueryInt(
                    request,
                    "pageSize",
                    10,
                    out var pageSize))
            {
                return false;
            }
            if (pageSize is < 1 or > 50)
                return false;
            if (pageSize <= 10)
            {
                candidates.Add(new(
                    $"rankings:overview:{metric}:10",
                    PublicApiCacheTransform
                        .OverviewSubset,
                    PageSize: pageSize));
            }
            else if (pageSize is 25 or 50)
            {
                normalizedRequestCacheKey =
                    $"rankings:overview:{metric}:{pageSize}";
                candidates.Add(new(
                    normalizedRequestCacheKey));
                lazy = true;
            }
            else
            {
                return false;
            }
            return true;
        }

        if (segments.Length == 3
            && string.Equals(
                segments[2],
                "composite",
                StringComparison.OrdinalIgnoreCase)
            && OnlyQueryKeys(
                request,
                "page",
                "pageSize"))
        {
            return AddFirstPageCandidate(
                request,
                candidates,
                "rankings:composite:adjusted:1:50");
        }

        if (segments.Length == 3
            && GlobalLeaderboardPersistence
                .TryGetCanonicalInstrument(
                    segments[2],
                    out var canonicalInstrument)
            && !request.Query.ContainsKey("leeway")
            && OnlyQueryKeys(
                request,
                "rankBy",
                "page",
                "pageSize"))
        {
            return AddFirstPageCandidate(
                request,
                candidates,
                $"rankings:{canonicalInstrument}:{metric}:1:50");
        }

        if (segments.Length == 4
            && string.Equals(
                segments[2],
                "bands",
                StringComparison.OrdinalIgnoreCase)
            && TryGetCanonicalBandType(
                segments[3],
                out var canonicalBandType)
            && !request.Query.ContainsKey("combo")
            && OnlyQueryKeys(
                request,
                "rankBy",
                "page",
                "pageSize"))
        {
            return AddFirstPageCandidate(
                request,
                candidates,
                $"rankings:bands:{canonicalBandType}:{metric}:1:50");
        }

        return false;
    }

    private static bool TryPlanPlayer(
        HttpRequest request,
        string[] segments,
        List<PublicApiCacheLookupCandidate> candidates)
    {
        if (segments.Length != 3
            || !string.Equals(
                segments[0],
                "api",
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                segments[1],
                "player",
                StringComparison.OrdinalIgnoreCase)
            || !OnlyQueryKeys(request))
        {
            return false;
        }

        candidates.Add(new(
            $"player:{segments[2]}:::"));
        return true;
    }

    private static bool TryPlanLeaderboard(
        HttpRequest request,
        string[] segments,
        List<PublicApiCacheLookupCandidate> candidates)
    {
        if (segments.Length < 4
            || !string.Equals(
                segments[0],
                "api",
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                segments[1],
                "leaderboard",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var songId = segments[2];
        if (segments.Length == 4
            && GlobalLeaderboardPersistence
                .TryGetCanonicalInstrument(
                    segments[3],
                    out var canonicalInstrument)
            && OnlyQueryKeys(
                request,
                "top",
                "offset",
                "leeway"))
        {
            if (!TryQueryInt(
                    request,
                    "top",
                    0,
                    out var top)
                || !TryQueryInt(
                    request,
                    "offset",
                    0,
                    out var offset)
                || !TryQueryDouble(
                    request,
                    "leeway",
                    out var leeway))
            {
                return false;
            }
            if (top != 10
                || offset != 0
                || leeway is not null)
            {
                return false;
            }
            candidates.Add(new(
                PublicationApiCacheKeys
                    .InstrumentLeaderboard(
                        songId,
                        canonicalInstrument,
                        top,
                        leeway)));
            return true;
        }

        if (segments.Length == 4
            && string.Equals(
                segments[3],
                "all",
                StringComparison.OrdinalIgnoreCase)
            && OnlyQueryKeys(
                request,
                "top",
                "leeway"))
        {
            if (!TryQueryInt(
                    request,
                    "top",
                    10,
                    out var top)
                || !TryQueryDouble(
                    request,
                    "leeway",
                    out var leeway))
            {
                return false;
            }
            if (top != 10
                || leeway is not (null or 1.0))
            {
                return false;
            }
            candidates.Add(new(
                LeaderboardCacheKeys.LeaderboardAll(
                    songId,
                    top,
                    leeway)));
            return true;
        }

        if (segments.Length == 5
            && string.Equals(
                segments[3],
                "bands",
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                segments[4],
                "all",
                StringComparison.OrdinalIgnoreCase)
            && OnlyQueryKeys(request, "top"))
        {
            if (!TryQueryInt(
                    request,
                    "top",
                    10,
                    out var top))
            {
                return false;
            }
            if (top != 10)
                return false;
            candidates.Add(new(
                LeaderboardCacheKeys
                    .SongBandLeaderboardsAll(
                        songId,
                        top)));
            return true;
        }

        return false;
    }

    private static bool AddFirstPageCandidate(
        HttpRequest request,
        List<PublicApiCacheLookupCandidate> candidates,
        string cacheKey)
    {
        if (!TryQueryInt(
                request,
                "page",
                1,
                out var page)
            || !TryQueryInt(
                request,
                "pageSize",
                50,
                out var pageSize))
        {
            return false;
        }
        if (page < 1
            || pageSize is < 1 or > 50
            || page > 50 / pageSize)
        {
            return false;
        }

        candidates.Add(new(
            cacheKey,
            PublicApiCacheTransform.FirstPageSubset,
            page,
            pageSize));
        return true;
    }

    private static bool HasSelectedOverlayQuery(
        HttpRequest request) =>
        request.Query.ContainsKey("accountId")
        || request.Query.ContainsKey("teamKey")
        || request.Query.ContainsKey(
            "selectedTeamKey")
        || request.Query.ContainsKey(
            "selectedBandType");

    private static bool IsProfileInvariantRequest(
        PathString path)
    {
        var segments = path.Value?.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        if (segments is null
            || segments.Length < 3
            || !string.Equals(
                segments[0],
                "api",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(
                segments[1],
                "rankings",
                StringComparison.OrdinalIgnoreCase))
        {
            return segments.Length == 3
                && (GlobalLeaderboardPersistence
                        .IsValidInstrument(segments[2])
                    || string.Equals(
                        segments[2],
                        "overview",
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        segments[2],
                        "composite",
                        StringComparison.OrdinalIgnoreCase))
                || segments.Length == 4
                && string.Equals(
                    segments[2],
                    "bands",
                    StringComparison.OrdinalIgnoreCase)
                && BandComboIds.IsValidBandType(
                    segments[3]);
        }

        return string.Equals(
                   segments[1],
                   "leaderboard",
                   StringComparison.OrdinalIgnoreCase)
               || segments.Length == 3
               && string.Equals(
                   segments[1],
                   "player",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetCanonicalBandType(
        string bandType,
        out string canonicalBandType)
    {
        canonicalBandType =
            BandInstrumentMapping.AllBandTypes
                .FirstOrDefault(candidate =>
                    string.Equals(
                        candidate,
                        bandType,
                        StringComparison.OrdinalIgnoreCase))
            ?? string.Empty;
        return canonicalBandType.Length > 0;
    }

    private static string BuildCanonicalQueryString(
        HttpRequest request)
    {
        var values = request.Query
            .Where(pair => !string.Equals(
                pair.Key,
                PublicationReadContextMiddleware
                    .PublicationQueryParameter,
                StringComparison.OrdinalIgnoreCase))
            .SelectMany(pair => pair.Value.Select(value =>
                new KeyValuePair<string, string?>(
                    pair.Key,
                    value)))
            .OrderBy(
                static pair => pair.Key,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(
                static pair => pair.Value,
                StringComparer.Ordinal);
        return QueryString.Create(values).Value
            ?? string.Empty;
    }

    private static bool OnlyQueryKeys(
        HttpRequest request,
        params string[] allowed)
    {
        var set = allowed.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        set.Add(
            PublicationReadContextMiddleware
                .PublicationQueryParameter);
        return request.Query.Keys.All(set.Contains);
    }

    private static string? QueryValue(
        HttpRequest request,
        string name) =>
        request.Query.TryGetValue(name, out var value)
            && value.Count == 1
            && !string.IsNullOrWhiteSpace(value[0])
                ? value[0]
                : null;

    private static bool TryQueryInt(
        HttpRequest request,
        string name,
        int fallback,
        out int value)
    {
        if (!request.Query.ContainsKey(name))
        {
            value = fallback;
            return true;
        }

        return int.TryParse(
            QueryValue(request, name),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out value);
    }

    private static bool TryQueryDouble(
        HttpRequest request,
        string name,
        out double? value)
    {
        if (!request.Query.ContainsKey(name))
        {
            value = null;
            return true;
        }

        if (double.TryParse(
            QueryValue(request, name),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed))
        {
            value = parsed;
            return true;
        }

        value = null;
        return false;
    }

    private static string HeaderValue(
        HttpRequest request,
        string headerName) =>
        request.Headers.TryGetValue(
            headerName,
            out var value)
                ? value.ToString()
                : string.Empty;
}
