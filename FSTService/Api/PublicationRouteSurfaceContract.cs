using FSTService.Persistence;

namespace FSTService.Api;

public sealed record PublicationRouteSurfaceFamily(
    string Name,
    IReadOnlyList<string> RequiredSurfaces);

public sealed record PublicationRouteSurfaceContract(
    string HttpMethod,
    string RoutePattern,
    string Family,
    IReadOnlyList<string> RequiredSurfaces)
{
    public override string ToString()
        => $"{HttpMethod} {RoutePattern} => {Family} " +
           $"[{string.Join(", ", RequiredSurfaces)}]";
}

public static class PublicationRouteSurfaceContractCatalog
{
    public const int ContractVersion = 1;

    private static readonly PublicationRouteSurfaceContract[] Definitions =
    [
        Route(HttpMethods.Post, "/api/account/name-refresh", "account-identity"),
        Route(HttpMethods.Get, "/api/account/search", "account-identity"),

        Route(HttpMethods.Get, "/api/shop", "shop"),
        Route(HttpMethods.Get, "/api/songs", "songs"),
        Route(
            HttpMethods.Get,
            "/api/songs/member-score-filter",
            "member-score-filter"),
        Route(
            HttpMethods.Get,
            "/api/paths/{songId}/{instrument}/{difficulty}",
            "paths"),
        Route(
            HttpMethods.Get,
            "/api/paths/{songId}/{instrument}/{difficulty}/data",
            "paths"),

        Route(
            HttpMethods.Get,
            "/api/leaderboard/{songId}/bands/all",
            "band-leaderboards"),
        Route(
            HttpMethods.Get,
            "/api/leaderboard/{songId}/bands/{bandType}",
            "band-leaderboards"),
        Route(
            HttpMethods.Get,
            "/api/leaderboard/{songId}/members/scores",
            "solo-leaderboards"),
        Route(
            HttpMethods.Get,
            "/api/leaderboard/{songId}/{instrument}",
            "solo-leaderboards"),
        Route(
            HttpMethods.Get,
            "/api/leaderboard-rank-offsets/{songId}/{instrument}",
            "solo-leaderboards"),
        Route(
            HttpMethods.Get,
            "/api/leaderboard/{songId}/all",
            "solo-leaderboards"),

        Route(HttpMethods.Get, "/api/player/{accountId}", "player-profile"),
        Route(
            HttpMethods.Get,
            "/api/player/{accountId}/stats",
            "player-profile"),
        Route(
            HttpMethods.Get,
            "/api/player/{accountId}/bands",
            "player-profile"),
        Route(
            HttpMethods.Get,
            "/api/player/{accountId}/bands/{bandType}",
            "player-profile"),
        Route(
            HttpMethods.Get,
            "/api/player/{accountId}/history",
            "player-profile"),
        Route(
            HttpMethods.Get,
            "/api/player/{accountId}/export",
            "player-export"),
        Route(
            HttpMethods.Get,
            "/api/bands/{bandType}/{teamKey}/export",
            "band-export"),

        Route(
            HttpMethods.Get,
            "/api/player/{accountId}/leaderboard-rivals/{instrument}",
            "rivals"),
        Route(
            HttpMethods.Get,
            "/api/player/{accountId}/leaderboard-rivals/{instrument}/{rivalId}",
            "rivals"),
        Route(
            HttpMethods.Get,
            "/api/player/{accountId}/rivals",
            "rivals"),
        Route(
            HttpMethods.Get,
            "/api/player/{accountId}/rivals/suggestions",
            "rivals"),
        Route(
            HttpMethods.Get,
            "/api/player/{accountId}/rivals/all",
            "rivals"),
        Route(
            HttpMethods.Get,
            "/api/player/{accountId}/rivals/{combo}",
            "rivals"),
        Route(
            HttpMethods.Get,
            "/api/player/{accountId}/rivals/{combo}/{rivalId}",
            "rivals"),
        Route(
            HttpMethods.Get,
            "/api/player/{accountId}/rivals/{rivalId}/songs/{instrument}",
            "rivals"),
        Route(
            HttpMethods.Get,
            "/api/player/{accountId}/notifications",
            "notifications"),
        Route(
            HttpMethods.Get,
            "/api/rankings/bands/{bandType}/{teamKey}/notifications",
            "notifications"),
        Route(
            HttpMethods.Get,
            "/api/bands/{bandId}/notifications",
            "notifications"),

        Route(
            HttpMethods.Get,
            "/api/rankings/selected-members",
            "rankings-selection"),
        Route(
            HttpMethods.Get,
            "/api/rankings/family/{scopeId}",
            "rankings-selection"),
        Route(
            HttpMethods.Get,
            "/api/rankings/family/{scopeId}/{accountId}",
            "rankings-selection"),
        Route(
            HttpMethods.Get,
            "/api/rankings/{instrument}",
            "solo-rankings"),
        Route(
            HttpMethods.Get,
            "/api/rankings/{instrument}/{accountId}",
            "solo-rankings"),
        Route(
            HttpMethods.Get,
            "/api/rankings/{instrument}/{accountId}/history",
            "solo-rankings"),
        Route(
            HttpMethods.Get,
            "/api/rankings/composite",
            "solo-rankings"),
        Route(
            HttpMethods.Get,
            "/api/rankings/composite/{accountId}",
            "solo-rankings"),
        Route(
            HttpMethods.Get,
            "/api/rankings/combo",
            "solo-rankings"),
        Route(
            HttpMethods.Get,
            "/api/rankings/combo/{accountId}",
            "solo-rankings"),
        Route(
            HttpMethods.Get,
            "/api/rankings/bands/{bandType}/combos",
            "band-rankings"),
        Route(
            HttpMethods.Get,
            "/api/rankings/bands/{bandType}",
            "band-rankings"),
        Route(HttpMethods.Get, "/api/bands/search", "band-directory"),
        Route(HttpMethods.Get, "/api/bands/{bandId}", "band-directory"),
        Route(
            HttpMethods.Get,
            "/api/rankings/bands/{bandType}/{teamKey}/history",
            "band-rankings"),
        Route(
            HttpMethods.Get,
            "/api/rankings/bands/{bandType}/{teamKey}/songs",
            "band-rankings"),
        Route(
            HttpMethods.Get,
            "/api/rankings/bands/{bandType}/{teamKey}/song-rows",
            "band-rankings"),
        Route(
            HttpMethods.Get,
            "/api/rankings/bands/{bandType}/{teamKey}",
            "band-rankings"),
        Route(
            HttpMethods.Get,
            "/api/rankings/{instrument}/{accountId}/neighborhood",
            "solo-rankings"),
        Route(
            HttpMethods.Get,
            "/api/rankings/composite/{accountId}/neighborhood",
            "solo-rankings"),
        Route(
            HttpMethods.Get,
            "/api/rankings/overview",
            "rankings-selection"),

        Route(HttpMethods.Get, "/api/firstseen", "first-seen"),
        Route(
            HttpMethods.Get,
            "/api/leaderboard-population",
            "leaderboard-population"),
        Route(ApiPublicationRouteCatalog.AnyMethod, "/api/ws", "websocket"),
    ];

    private static readonly PublicationRouteSurfaceFamily[] FamilyDefinitions =
    [
        Family(
            "account-identity",
            PublicationSurfaceNames.AccountNames),
        Family(
            "shop",
            PublicationSurfaceNames.SongCatalog,
            PublicationSurfaceNames.ItemShop),
        Family(
            "songs",
            PublicationSurfaceNames.SongCatalog,
            PublicationSurfaceNames.PathArtifacts,
            PublicationSurfaceNames.SoloScopeSources,
            PublicationSurfaceNames.History),
        Family(
            "member-score-filter",
            PublicationSurfaceNames.SoloScopeSources),
        Family(
            "paths",
            PublicationSurfaceNames.SongCatalog,
            PublicationSurfaceNames.PathArtifacts),
        Family(
            "band-leaderboards",
            PublicationSurfaceNames.SongCatalog,
            PublicationSurfaceNames.BandRankings,
            PublicationSurfaceNames.AccountNames),
        Family(
            "solo-leaderboards",
            PublicationSurfaceNames.SongCatalog,
            PublicationSurfaceNames.SoloScopeSources,
            PublicationSurfaceNames.AccountNames),
        Family(
            "player-profile",
            PublicationSurfaceNames.SongCatalog,
            PublicationSurfaceNames.SoloScopeSources,
            PublicationSurfaceNames.BandRankings,
            PublicationSurfaceNames.History,
            PublicationSurfaceNames.AccountNames,
            PublicationSurfaceNames.AccountOverlays),
        Family(
            "player-export",
            PublicationSurfaceNames.SongCatalog,
            PublicationSurfaceNames.SoloScopeSources,
            PublicationSurfaceNames.History,
            PublicationSurfaceNames.AccountNames),
        Family(
            "band-export",
            PublicationSurfaceNames.SongCatalog,
            PublicationSurfaceNames.BandRankings,
            PublicationSurfaceNames.History,
            PublicationSurfaceNames.AccountNames),
        Family(
            "rivals",
            PublicationSurfaceNames.SongCatalog,
            PublicationSurfaceNames.SoloScopeSources,
            PublicationSurfaceNames.AccountOverlays,
            PublicationSurfaceNames.AccountNames),
        Family(
            "notifications",
            PublicationSurfaceNames.ImprovementNotifications,
            PublicationSurfaceNames.History,
            PublicationSurfaceNames.AccountNames),
        Family(
            "solo-rankings",
            PublicationSurfaceNames.SoloScopeSources,
            PublicationSurfaceNames.History,
            PublicationSurfaceNames.AccountNames,
            PublicationSurfaceNames.AccountOverlays),
        Family(
            "band-rankings",
            PublicationSurfaceNames.BandRankings,
            PublicationSurfaceNames.History,
            PublicationSurfaceNames.AccountNames,
            PublicationSurfaceNames.AccountOverlays),
        Family(
            "rankings-selection",
            PublicationSurfaceNames.SoloScopeSources,
            PublicationSurfaceNames.BandRankings,
            PublicationSurfaceNames.History,
            PublicationSurfaceNames.AccountNames,
            PublicationSurfaceNames.AccountOverlays),
        Family(
            "band-directory",
            PublicationSurfaceNames.BandRankings,
            PublicationSurfaceNames.History,
            PublicationSurfaceNames.AccountNames),
        Family(
            "first-seen",
            PublicationSurfaceNames.SongCatalog,
            PublicationSurfaceNames.History),
        Family(
            "leaderboard-population",
            PublicationSurfaceNames.SoloScopeSources),
        Family(
            "websocket",
            PublicationSurfaceNames.ImprovementNotifications,
            PublicationSurfaceNames.SongCatalog,
            PublicationSurfaceNames.ItemShop),
    ];

    private static readonly IReadOnlyDictionary<string, PublicationRouteSurfaceFamily>
        FamilyByName = FamilyDefinitions.ToDictionary(
            static family => family.Name,
            StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, PublicationRouteSurfaceContract>
        ContractByRoute = BuildContractIndex(Definitions);

    public static IReadOnlyList<PublicationRouteSurfaceFamily> Families { get; } =
        Array.AsReadOnly(FamilyDefinitions);

    public static IReadOnlyList<PublicationRouteSurfaceContract> Routes { get; } =
        Array.AsReadOnly(Definitions);

    public static IReadOnlyList<string> RequiredSurfaceNames { get; } =
        Definitions
            .SelectMany(static contract => contract.RequiredSurfaces)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    internal static PublicationRouteSurfaceContract Resolve(
        string httpMethod,
        string routePattern)
    {
        var key = RouteKey(httpMethod, routePattern);
        if (ContractByRoute.TryGetValue(key, out var contract))
            return contract;

        throw new InvalidOperationException(
            $"Publication-bound route {httpMethod} {routePattern} has no surface contract.");
    }

    internal static void Validate(
        IEnumerable<ClassifiedApiRouteDescription> classifiedRoutes)
    {
        var failures = GetValidationFailures(
            classifiedRoutes,
            Definitions);
        if (failures.Count == 0)
            return;

        throw new InvalidOperationException(
            "Publication route surface contract validation failed:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, failures));
    }

    internal static IReadOnlyList<string> GetValidationFailures(
        IEnumerable<ClassifiedApiRouteDescription> classifiedRoutes,
        IEnumerable<PublicationRouteSurfaceContract> contracts)
    {
        var routeKeys = classifiedRoutes
            .Where(static route => route.Classification is PublicationBound)
            .SelectMany(route => route.HttpMethods.Select(method =>
                RouteKey(method, route.RoutePattern)))
            .ToArray();
        var contractArray = contracts.ToArray();
        var contractKeys = contractArray
            .Select(static contract =>
                RouteKey(contract.HttpMethod, contract.RoutePattern))
            .ToArray();
        var failures = new List<string>();

        failures.AddRange(contractKeys
            .GroupBy(static key => key, StringComparer.Ordinal)
            .Where(static group => group.Count() != 1)
            .Select(group =>
                $"Duplicate surface contract {group.Key} appears {group.Count()} times."));

        failures.AddRange(routeKeys
            .Except(contractKeys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(static key =>
                $"Publication-bound route {key} has no surface contract."));

        failures.AddRange(contractKeys
            .Except(routeKeys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(static key =>
                $"Surface contract {key} has no PublicationBound route."));

        foreach (var contract in contractArray.OrderBy(
                     static contract => RouteKey(
                         contract.HttpMethod,
                         contract.RoutePattern),
                     StringComparer.Ordinal))
        {
            if (!FamilyByName.TryGetValue(contract.Family, out var family))
            {
                failures.Add(
                    $"{contract.HttpMethod} {contract.RoutePattern} uses unknown family {contract.Family}.");
                continue;
            }

            if (contract.RequiredSurfaces.Count == 0)
            {
                failures.Add(
                    $"{contract.HttpMethod} {contract.RoutePattern} has no required surfaces.");
            }

            var duplicateSurfaces = contract.RequiredSurfaces
                .GroupBy(static surface => surface, StringComparer.Ordinal)
                .Where(static group => group.Count() != 1)
                .Select(static group => group.Key)
                .Order(StringComparer.Ordinal);
            foreach (var surface in duplicateSurfaces)
            {
                failures.Add(
                    $"{contract.HttpMethod} {contract.RoutePattern} repeats surface {surface}.");
            }

            foreach (var surface in contract.RequiredSurfaces
                         .Where(surface =>
                             !PublicationSurfaceContractCatalog
                                 .KnownSurfaceNames.Contains(surface))
                         .Order(StringComparer.Ordinal))
            {
                failures.Add(
                    $"{contract.HttpMethod} {contract.RoutePattern} uses unknown surface {surface}.");
            }

            if (!contract.RequiredSurfaces.SequenceEqual(
                    family.RequiredSurfaces,
                    StringComparer.Ordinal))
            {
                failures.Add(
                    $"{contract.HttpMethod} {contract.RoutePattern} does not match family {contract.Family}.");
            }
        }

        return failures
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static PublicationRouteSurfaceFamily Family(
        string name,
        params string[] requiredSurfaces)
        => new(
            name,
            Array.AsReadOnly(requiredSurfaces));

    private static PublicationRouteSurfaceContract Route(
        string method,
        string routePattern,
        string family)
        => new(
            method,
            routePattern,
            family,
            FamilySurfaces(family));

    private static IReadOnlyList<string> FamilySurfaces(string family)
        => family switch
        {
            "account-identity" =>
                [PublicationSurfaceNames.AccountNames],
            "shop" =>
                [
                    PublicationSurfaceNames.SongCatalog,
                    PublicationSurfaceNames.ItemShop,
                ],
            "songs" =>
                [
                    PublicationSurfaceNames.SongCatalog,
                    PublicationSurfaceNames.PathArtifacts,
                    PublicationSurfaceNames.SoloScopeSources,
                    PublicationSurfaceNames.History,
                ],
            "member-score-filter" =>
                [PublicationSurfaceNames.SoloScopeSources],
            "paths" =>
                [
                    PublicationSurfaceNames.SongCatalog,
                    PublicationSurfaceNames.PathArtifacts,
                ],
            "band-leaderboards" =>
                [
                    PublicationSurfaceNames.SongCatalog,
                    PublicationSurfaceNames.BandRankings,
                    PublicationSurfaceNames.AccountNames,
                ],
            "solo-leaderboards" =>
                [
                    PublicationSurfaceNames.SongCatalog,
                    PublicationSurfaceNames.SoloScopeSources,
                    PublicationSurfaceNames.AccountNames,
                ],
            "player-profile" =>
                [
                    PublicationSurfaceNames.SongCatalog,
                    PublicationSurfaceNames.SoloScopeSources,
                    PublicationSurfaceNames.BandRankings,
                    PublicationSurfaceNames.History,
                    PublicationSurfaceNames.AccountNames,
                    PublicationSurfaceNames.AccountOverlays,
                ],
            "player-export" =>
                [
                    PublicationSurfaceNames.SongCatalog,
                    PublicationSurfaceNames.SoloScopeSources,
                    PublicationSurfaceNames.History,
                    PublicationSurfaceNames.AccountNames,
                ],
            "band-export" =>
                [
                    PublicationSurfaceNames.SongCatalog,
                    PublicationSurfaceNames.BandRankings,
                    PublicationSurfaceNames.History,
                    PublicationSurfaceNames.AccountNames,
                ],
            "rivals" =>
                [
                    PublicationSurfaceNames.SongCatalog,
                    PublicationSurfaceNames.SoloScopeSources,
                    PublicationSurfaceNames.AccountOverlays,
                    PublicationSurfaceNames.AccountNames,
                ],
            "notifications" =>
                [
                    PublicationSurfaceNames.ImprovementNotifications,
                    PublicationSurfaceNames.History,
                    PublicationSurfaceNames.AccountNames,
                ],
            "solo-rankings" =>
                [
                    PublicationSurfaceNames.SoloScopeSources,
                    PublicationSurfaceNames.History,
                    PublicationSurfaceNames.AccountNames,
                    PublicationSurfaceNames.AccountOverlays,
                ],
            "band-rankings" =>
                [
                    PublicationSurfaceNames.BandRankings,
                    PublicationSurfaceNames.History,
                    PublicationSurfaceNames.AccountNames,
                    PublicationSurfaceNames.AccountOverlays,
                ],
            "rankings-selection" =>
                [
                    PublicationSurfaceNames.SoloScopeSources,
                    PublicationSurfaceNames.BandRankings,
                    PublicationSurfaceNames.History,
                    PublicationSurfaceNames.AccountNames,
                    PublicationSurfaceNames.AccountOverlays,
                ],
            "band-directory" =>
                [
                    PublicationSurfaceNames.BandRankings,
                    PublicationSurfaceNames.History,
                    PublicationSurfaceNames.AccountNames,
                ],
            "first-seen" =>
                [
                    PublicationSurfaceNames.SongCatalog,
                    PublicationSurfaceNames.History,
                ],
            "leaderboard-population" =>
                [PublicationSurfaceNames.SoloScopeSources],
            "websocket" =>
                [
                    PublicationSurfaceNames.ImprovementNotifications,
                    PublicationSurfaceNames.SongCatalog,
                    PublicationSurfaceNames.ItemShop,
                ],
            _ => throw new InvalidOperationException(
                $"Unknown publication route surface family {family}."),
        };

    private static IReadOnlyDictionary<string, PublicationRouteSurfaceContract>
        BuildContractIndex(
            IEnumerable<PublicationRouteSurfaceContract> contracts)
    {
        var result =
            new Dictionary<string, PublicationRouteSurfaceContract>(
                StringComparer.Ordinal);
        foreach (var contract in contracts)
        {
            var key = RouteKey(contract.HttpMethod, contract.RoutePattern);
            if (!result.TryAdd(key, contract))
            {
                throw new InvalidOperationException(
                    $"Duplicate publication route surface contract for {key}.");
            }
        }

        return result;
    }

    private static string RouteKey(string httpMethod, string routePattern)
        => $"{httpMethod.ToUpperInvariant()} " +
           ApiPublicationEndpointDescriptions.CanonicalizeRoutePattern(
               routePattern);
}
