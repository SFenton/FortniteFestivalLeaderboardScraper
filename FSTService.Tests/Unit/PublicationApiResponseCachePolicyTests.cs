using FSTService.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace FSTService.Tests.Unit;

public sealed class PublicationApiResponseCachePolicyTests
{
    [Fact]
    public void Eligibility_requires_exactly_one_publication_bound_classification()
    {
        var unclassified = Context(
            "/api/songs",
            endpointMetadata: []);
        var privateRoute = Context(
            "/api/private-cache-probe",
            endpointMetadata:
            [
                new AdminPrivate(
                    "private test route"),
            ]);
        var conflicting = Context(
            "/api/songs",
            endpointMetadata:
            [
                PublicationBound.Instance,
                new AdminPrivate(
                    "conflicting route drift"),
            ]);

        Assert.False(
            PublicApiResponseCachePolicy
                .TryCreateRequestPlan(
                    unclassified,
                    out _));
        Assert.False(
            PublicApiResponseCachePolicy
                .TryCreateRequestPlan(
                    privateRoute,
                    out _));
        Assert.False(
            PublicApiResponseCachePolicy
                .TryCreateRequestPlan(
                    conflicting,
                    out _));
    }

    [Theory]
    [InlineData("/api/songs", "public-api:songs:v1")]
    [InlineData(
        "/api/rankings/overview",
        "rankings:overview:adjusted:10")]
    [InlineData(
        "/api/rankings/composite?page=1&pageSize=5",
        "rankings:composite:adjusted:1:50")]
    [InlineData(
        "/api/rankings/bands/Band_Duets?page=1&pageSize=5",
        "rankings:bands:Band_Duets:adjusted:1:50")]
    [InlineData(
        "/api/rankings/Solo_Guitar?page=1&pageSize=5",
        "rankings:Solo_Guitar:adjusted:1:50")]
    [InlineData(
        "/api/player/account-1",
        "player:account-1:::")]
    [InlineData(
        "/api/leaderboard/song-1/Solo_Guitar?top=10",
        "leaderboard:instrument:song-1:Solo_Guitar:10:")]
    public void Freeze_critical_routes_resolve_canonical_keys(
        string target,
        string canonicalKey)
    {
        var context = Context(target);

        Assert.True(
            PublicApiResponseCachePolicy.TryCreateRequestPlan(
                context,
                out var plan));
        Assert.True(plan.FreezeCritical);
        Assert.Contains(
            plan.LookupCandidates,
            candidate => candidate.CacheKey == canonicalKey);
    }

    [Theory]
    [InlineData(
        "/api/songs",
        "public, max-age=1800, stale-while-revalidate=3600")]
    [InlineData(
        "/api/rankings/overview?pageSize=25",
        "public, max-age=1800, stale-while-revalidate=3600")]
    [InlineData(
        "/api/player/account-1",
        "public, max-age=120, stale-while-revalidate=300")]
    [InlineData(
        "/api/leaderboard/song-1/Solo_Guitar?top=10",
        "public, max-age=300")]
    [InlineData(
        "/api/leaderboard/song-1/all?top=10",
        "public, max-age=300, stale-while-revalidate=600")]
    [InlineData(
        "/api/leaderboard/song-1/bands/all?top=10",
        "public, max-age=300, stale-while-revalidate=600")]
    public void Covered_routes_preserve_endpoint_cache_control(
        string target,
        string expected)
    {
        var context = Context(target);

        Assert.True(
            PublicApiResponseCachePolicy.TryCreateRequestPlan(
                context,
                out var plan));
        Assert.Equal(
            expected,
            plan.ResponseCacheControl);
    }

    [Fact]
    public void Songs_key_ignores_nonsemantic_query_and_publication_pin()
    {
        var plain = Context("/api/songs");
        var query = Context(
            "/api/songs?limit=10&publicationId=42");

        PublicApiResponseCachePolicy.TryCreateRequestPlan(
            plain,
            out var plainPlan);
        PublicApiResponseCachePolicy.TryCreateRequestPlan(
            query,
            out var queryPlan);

        Assert.Equal(
            plainPlan.RequestCacheKey,
            queryPlan.RequestCacheKey);
        Assert.Equal(
            "public-route:/api/songs|profileType=|profileId="
            + "|legacyPlayer=|bandId=|bandType=|teamKey=",
            plainPlan.RequestCacheKey);
    }

    [Theory]
    [InlineData("/api/rankings/overview?pageSize=51")]
    [InlineData("/api/rankings/overview?pageSize=11")]
    [InlineData("/api/rankings/overview?pageSize=invalid")]
    [InlineData("/api/rankings/overview?pageSize=25&pageSize=50")]
    [InlineData("/api/rankings/composite?page=6&pageSize=10")]
    [InlineData(
        "/api/rankings/bands/Band_Duets?accountId=account-1")]
    [InlineData("/api/player/account-1?leeway=1")]
    [InlineData(
        "/api/leaderboard/song-1/Solo_Guitar?top=50")]
    [InlineData(
        "/api/leaderboard/song-1/Solo_Guitar?top=10&leeway=1")]
    public void High_cardinality_or_unbounded_variants_are_not_covered(
        string target)
    {
        var context = Context(target);

        var cacheable =
            PublicApiResponseCachePolicy.TryCreateRequestPlan(
                context,
                out var plan);
        Assert.True(!cacheable || !plan.FreezeCritical);
    }

    [Fact]
    public void Overview_above_eager_page_size_is_lazy_and_bounded()
    {
        var context = Context(
            "/api/rankings/overview?pageSize=25&rankBy=weighted");

        PublicApiResponseCachePolicy.TryCreateRequestPlan(
            context,
            out var plan);

        Assert.True(plan.FreezeCritical);
        Assert.True(plan.AllowWriteThrough);
        Assert.Equal(TimeSpan.FromSeconds(1), plan.MaxBuildDuration);
    }

    [Fact]
    public void Lazy_overview_variants_use_one_semantic_key()
    {
        var canonical = Context(
            "/api/rankings/overview?rankBy=weighted&pageSize=25");
        var lexicalVariant = Context(
            "/API/RANKINGS/OVERVIEW?pageSize=025&rankBy=WEIGHTED");

        Assert.True(
            PublicApiResponseCachePolicy.TryCreateRequestPlan(
                canonical,
                out var canonicalPlan));
        Assert.True(
            PublicApiResponseCachePolicy.TryCreateRequestPlan(
                lexicalVariant,
                out var variantPlan));

        Assert.Equal(
            "rankings:overview:weighted:25",
            canonicalPlan.RequestCacheKey);
        Assert.Equal(
            canonicalPlan.RequestCacheKey,
            variantPlan.RequestCacheKey);
        Assert.Contains(
            variantPlan.LookupCandidates,
            candidate =>
                candidate.CacheKey
                == canonicalPlan.RequestCacheKey);
    }

    [Theory]
    [InlineData(
        "/api/rankings/solo_guitar?rankBy=WEIGHTED",
        "rankings:Solo_Guitar:weighted:1:50")]
    [InlineData(
        "/api/rankings/bands/band_duets",
        "rankings:bands:Band_Duets:adjusted:1:50")]
    [InlineData(
        "/api/leaderboard/song-1/solo_guitar?top=10",
        "leaderboard:instrument:song-1:Solo_Guitar:10:")]
    public void Canonical_aliases_normalize_route_value_casing(
        string target,
        string canonicalKey)
    {
        var context = Context(target);

        Assert.True(
            PublicApiResponseCachePolicy.TryCreateRequestPlan(
                context,
                out var plan));
        Assert.Contains(
            plan.LookupCandidates,
            candidate => candidate.CacheKey == canonicalKey);
    }

    [Fact]
    public void Generic_overview_key_ignores_selected_profile_headers()
    {
        var plain = Context("/api/rankings/overview");
        var selected = Context("/api/rankings/overview");
        selected.Request.Headers[
            SelectedProfileHeaders
                .SelectedProfileTypeHeader] = "player";
        selected.Request.Headers[
            SelectedProfileHeaders
                .SelectedProfileIdHeader] = "account-secret";

        Assert.Equal(
            PublicApiResponseCachePolicy.BuildCacheKey(
                plain.Request),
            PublicApiResponseCachePolicy.BuildCacheKey(
                selected.Request));
    }

    [Theory]
    [InlineData(
        "/api/rankings/bands/Band_Duets/team%3Aa%3Ab",
        "public, max-age=300")]
    [InlineData(
        "/api/rankings/bands/Band_Duets/team%3Aa%3Ab/history",
        "public, max-age=300")]
    [InlineData(
        "/api/rankings/bands/Band_Duets/team%3Aa%3Ab/songs?limit=5",
        "public, max-age=300")]
    [InlineData(
        "/api/rankings/bands/Band_Duets/team%3Aa%3Ab/song-rows",
        "public, max-age=300")]
    [InlineData(
        "/api/rankings/Solo_Guitar/account-1",
        "public, max-age=300")]
    [InlineData(
        "/api/rankings/Solo_Guitar/account-1/neighborhood",
        "public, max-age=300, stale-while-revalidate=600")]
    [InlineData(
        "/api/rankings/composite/account-1",
        "public, max-age=300")]
    [InlineData(
        "/api/rankings/composite/account-1/neighborhood",
        "public, max-age=300, stale-while-revalidate=600")]
    public void Identity_scoped_profile_reads_share_cache_key_across_selected_profiles(
        string target,
        string expectedCacheControl)
    {
        var plain = Context(target);
        var selected = Context(target);
        selected.Request.Headers[
            SelectedProfileHeaders.SelectedProfileTypeHeader] =
            "band";
        selected.Request.Headers[
            SelectedProfileHeaders.SelectedProfileIdHeader] =
            "selected-band-id";
        selected.Request.Headers[
            SelectedProfileHeaders.SelectedBandTypeHeader] =
            "Band_Duets";
        selected.Request.Headers[
            SelectedProfileHeaders.SelectedBandTeamKeyHeader] =
            "team:a:b";

        Assert.True(
            PublicApiResponseCachePolicy.TryCreateRequestPlan(
                plain,
                out var plan));
        Assert.True(plan.FreezeCritical);
        Assert.True(plan.AllowWriteThrough);
        Assert.Equal(
            expectedCacheControl,
            plan.ResponseCacheControl);
        Assert.Equal(
            PublicApiResponseCachePolicy.BuildCacheKey(
                plain.Request),
            PublicApiResponseCachePolicy.BuildCacheKey(
                selected.Request));
    }

    [Theory]
    [InlineData(
        "/api/rankings/bands/Band_Duets/team%3Aa%3Ab/songs?limit=10")]
    [InlineData(
        "/api/rankings/bands/Band_Duets/team%3Aa%3Ab/songs?limit=5&combo=Solo_Guitar")]
    [InlineData(
        "/api/rankings/bands/Band_Duets/team%3Aa%3Ab/history?days=30")]
    [InlineData(
        "/api/rankings/Solo_Guitar/account-1/neighborhood?radius=5")]
    [InlineData("/api/rankings/bands/Band_Duets/combos")]
    public void Identity_scoped_profile_cache_excludes_nondefault_variants(
        string target)
    {
        var context = Context(target);

        Assert.True(
            PublicApiResponseCachePolicy.TryCreateRequestPlan(
                context,
                out var plan));
        Assert.False(plan.FreezeCritical);
        Assert.False(plan.AllowWriteThrough);
    }

    private static DefaultHttpContext Context(
        string target,
        object[]? endpointMetadata = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        var separator = target.IndexOf('?');
        context.Request.Path = separator < 0
            ? target
            : target[..separator];
        if (separator >= 0)
        {
            context.Request.QueryString =
                new QueryString(target[separator..]);
        }
        var metadata = new List<object>
        {
            new HttpMethodMetadata(
                [HttpMethods.Get]),
        };
        metadata.AddRange(
            endpointMetadata
            ??
            [
                PublicationBound.Instance,
            ]);
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(
                context.Request.Path.Value!),
            order: 0,
            new EndpointMetadataCollection(
                metadata),
            displayName: target));
        return context;
    }
}
