using System.Text;
using System.Text.Json;
using FortniteFestival.Core;
using FortniteFestival.Core.Services;
using FSTService.Api;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FSTService.Tests.Unit;

/// <summary>
/// Publication-bound songs cache ownership. In publication-bound mode the
/// durable <c>public-api:songs:v1</c> row belongs to the publication pipeline:
/// this process hydrates from it and never rewrites it from process-local
/// state (for example an API role whose precomputer has no population tiers).
/// </summary>
public sealed class PublicationBoundSongsCacheTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();

    private MetaDatabase Db => _fixture.Db;
    private NpgsqlDataSource DataSource => _fixture.DataSource;

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Publication_bound_startup_prime_preserves_the_durable_payload()
    {
        var publicationId = await PublishAsync("song-a");
        var rich = StoreRichDurableSongsPayload(publicationId);
        var cache = CreateCache(publicationBoundReads: true, publicationId);

        Prime(cache);

        // The durable row is untouched, byte for byte.
        var after = ReadDurableSongs(publicationId);
        Assert.Equal(rich.Json, after.Json);
        Assert.Equal(rich.ETag, after.ETag);

        // The in-process cache serves exactly the durable payload.
        var served = cache.Get();
        Assert.NotNull(served);
        Assert.Equal(rich.Json, served!.Value.Json);
        Assert.Equal(rich.ETag, served.Value.ETag);
        Assert.Contains(
            "populationTiers",
            Encoding.UTF8.GetString(served.Value.Json),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publication_bound_catalog_refresh_does_not_rewrite_the_durable_payload()
    {
        var publicationId = await PublishAsync("song-a");
        var rich = StoreRichDurableSongsPayload(publicationId);
        var cache = CreateCache(publicationBoundReads: true, publicationId);
        Prime(cache);

        // Same-publication catalog refresh path.
        cache.InvalidateForContentChange();
        Prime(cache);

        var after = ReadDurableSongs(publicationId);
        Assert.Equal(rich.Json, after.Json);
        Assert.Equal(rich.ETag, after.ETag);

        var served = cache.Get();
        Assert.NotNull(served);
        Assert.Equal(rich.Json, served!.Value.Json);
        Assert.Equal(rich.ETag, served.Value.ETag);
        Assert.False(cache.DurableRefreshPending);
    }

    [Fact]
    public async Task Publication_bound_content_mutation_does_not_rewrite_the_durable_payload()
    {
        var publicationId = await PublishAsync("song-a");
        var rich = StoreRichDurableSongsPayload(publicationId);
        var cache = CreateCache(publicationBoundReads: true, publicationId);

        using (cache.BeginContentMutation())
        {
        }

        Prime(cache);

        var after = ReadDurableSongs(publicationId);
        Assert.Equal(rich.Json, after.Json);
        Assert.Equal(rich.ETag, after.ETag);
        Assert.Equal(rich.Json, cache.Get()!.Value.Json);
    }

    [Fact]
    public async Task Publication_bound_prime_without_a_durable_row_uses_bound_catalog_and_never_persists()
    {
        var publicationId = await PublishAsync("song-a");
        Assert.Null(TryReadDurableSongs(publicationId));
        var cache = CreateCache(publicationBoundReads: true, publicationId);

        Prime(cache, CreateFestivalService("song-b"));

        // L1 is built from the bound publication, not the newer live catalog.
        var served = cache.Get();
        Assert.NotNull(served);
        using var document = JsonDocument.Parse(served!.Value.Json);
        var songs = document.RootElement.GetProperty("songs");
        Assert.Single(songs.EnumerateArray());
        Assert.Equal(
            "song-a",
            songs[0].GetProperty("songId").GetString());
        Assert.Null(TryReadDurableSongs(publicationId));
    }

    [Fact]
    public async Task Publication_bound_hydration_rejects_a_mismatched_durable_etag()
    {
        var publicationId = await PublishAsync("song-a");
        var rich = StoreRichDurableSongsPayload(publicationId);
        ExecuteNonQuery(
            """
            UPDATE publication_api_response_cache
            SET etag = '"not-the-real-etag"'
            WHERE publication_id = @publicationId
              AND cache_key = @cacheKey
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "publicationId",
                    publicationId);
                command.Parameters.AddWithValue(
                    "cacheKey",
                    PublicationApiCacheKeys.Songs);
            });
        var cache = CreateCache(publicationBoundReads: true, publicationId);

        Assert.False(cache.TryHydrateFromDurablePublicationCache());

        Prime(cache);

        // The bound-publication build populated L1 only; the durable bytes
        // stayed put.
        Assert.NotNull(cache.Get());
        Assert.NotEqual(rich.Json, cache.Get()!.Value.Json);
        Assert.Equal(rich.Json, ReadDurableSongs(publicationId).Json);
    }

    [Fact]
    public async Task Live_mode_still_rewrites_the_durable_payload()
    {
        var publicationId = await PublishAsync("song-a");
        var rich = StoreRichDurableSongsPayload(publicationId);
        var cache = CreateCache(publicationBoundReads: false, publicationId);

        Prime(cache);

        var after = ReadDurableSongs(publicationId);
        Assert.NotEqual(rich.Json, after.Json);
        Assert.Equal(cache.Get()!.Value.Json, after.Json);
        Assert.Equal(cache.Get()!.Value.ETag, after.ETag);
    }

    [Fact]
    public async Task Publication_bound_hydration_retries_a_publication_pointer_move()
    {
        // Publication A is stale from this process's point of view: the
        // durable row already belongs to publication B.
        var publicationA = await PublishAsync("song-a");
        var publicationB = await PublishSecondAsync("song-a");
        Assert.NotEqual(publicationA, publicationB);
        var rich = StoreRichDurableSongsPayload(publicationB);

        // The first pointer read still returns A, later reads return B.
        var reads = 0;
        var cache = CreateCache(
            publicationBoundReads: true,
            () => Interlocked.Increment(ref reads) <= 1
                ? publicationA
                : publicationB);

        Assert.True(cache.TryHydrateFromDurablePublicationCache());
        Assert.True(reads > 1, "The pointer move must be retried.");

        var served = cache.Get();
        Assert.NotNull(served);
        Assert.Equal(rich.Json, served!.Value.Json);
        Assert.Equal(rich.ETag, served.Value.ETag);

        // No local fallback happened, so the durable row is untouched.
        var after = ReadDurableSongs(publicationB);
        Assert.Equal(rich.Json, after.Json);
        Assert.Equal(rich.ETag, after.ETag);
    }

    [Fact]
    public async Task Publication_bound_hydration_gives_up_after_bounded_attempts()
    {
        var publicationA = await PublishAsync("song-a");
        var publicationB = await PublishSecondAsync("song-a");
        StoreRichDurableSongsPayload(publicationB);

        // The pointer never settles on the publication that owns the row.
        var reads = 0;
        var cache = CreateCache(
            publicationBoundReads: true,
            () =>
            {
                Interlocked.Increment(ref reads);
                return publicationA;
            });

        Assert.False(cache.TryHydrateFromDurablePublicationCache());
        Assert.True(
            reads >= SongsCacheService.MaxHydrationAttempts,
            "Hydration must be bounded, not unbounded.");
    }

    [Fact]
    public async Task Publication_bound_songs_plan_ignores_and_never_writes_route_keys()
    {
        var publicationId = await PublishAsync("song-a");
        var canonical = StoreRichDurableSongsPayload(publicationId);
        var routeKey = StoreDegradedRouteKeyPayload(publicationId);

        var context = SongsRequestContext();
        Assert.True(
            PublicApiResponseCachePolicy.TryCreateRequestPlan(
                context,
                out var plan));

        Assert.True(plan.SongsRoute);

        // Flag off: the route key is looked up first and would shadow the
        // canonical row.
        Assert.StartsWith(
            PublicApiResponseCachePolicy.SongsRouteCacheKeyPrefix,
            plan.RequestCacheKey,
            StringComparison.Ordinal);
        Assert.True(plan.AllowWriteThrough);
        var liveCache = CreatePublicationCache(publicationId);
        var liveHit = liveCache.TryGetCurrent(plan);
        Assert.NotNull(liveHit);
        Assert.Equal(routeKey.Json, liveHit!.Value.Json);

        // Publication-bound: only the canonical key is readable, and
        // write-through is disabled so a degraded endpoint build can never
        // create a route-key row.
        var boundPlan = PublicApiResponseCachePolicy
            .ToPublicationBoundSongsPlan(plan);
        Assert.Equal(
            PublicationApiCacheKeys.Songs,
            boundPlan.RequestCacheKey);
        Assert.Equal(
            [PublicationApiCacheKeys.Songs],
            boundPlan.LookupCandidates
                .Select(static candidate => candidate.CacheKey));
        Assert.False(boundPlan.AllowWriteThrough);

        var boundCache = CreatePublicationCache(publicationId);
        var boundHit = boundCache.TryGetCurrent(boundPlan);
        Assert.NotNull(boundHit);
        Assert.Equal(canonical.Json, boundHit!.Value.Json);
        Assert.Equal(canonical.ETag, boundHit.Value.ETag);
    }

    [Fact]
    public async Task Route_key_rows_are_purged_for_publication_bound_rollout()
    {
        var publicationId = await PublishAsync("song-a");
        var canonical = StoreRichDurableSongsPayload(publicationId);
        StoreDegradedRouteKeyPayload(publicationId);
        Assert.Equal(1, CountRouteKeyRows());

        var purged = Db.PurgeApiResponseCacheKeysWithPrefix(
            PublicApiResponseCachePolicy.SongsRouteCacheKeyPrefix);

        Assert.True(purged >= 1);
        Assert.Equal(0, CountRouteKeyRows());
        var after = ReadDurableSongs(publicationId);
        Assert.Equal(canonical.Json, after.Json);
        Assert.Equal(canonical.ETag, after.ETag);
    }

    [Fact]
    public async Task Publication_bound_middleware_never_writes_a_route_key_row()
    {
        // No canonical row yet, so the middleware must actually build.
        var publicationId = await PublishAsync("song-a");
        var degraded = Encoding.UTF8.GetBytes(
            """{"count":1,"currentSeason":14,"songs":[{"songId":"song-a"}]}""");

        await InvokeSongsMiddlewareAsync(
            publicationId,
            publicationBoundReads: true,
            degraded);

        // The degraded endpoint build was served but never persisted, under
        // the route key or the canonical key.
        Assert.Equal(0, CountRouteKeyRows());
        Assert.Null(TryReadDurableSongs(publicationId));
    }

    [Fact]
    public async Task Publication_bound_middleware_serves_the_canonical_row()
    {
        var publicationId = await PublishAsync("song-a");
        var canonical = StoreRichDurableSongsPayload(publicationId);
        StoreDegradedRouteKeyPayload(publicationId);

        var served = await InvokeSongsMiddlewareAsync(
            publicationId,
            publicationBoundReads: true,
            Encoding.UTF8.GetBytes("""{"count":0}"""));

        // The pre-existing route-key row must not shadow the canonical row.
        Assert.Equal(canonical.Json, served);
        var after = ReadDurableSongs(publicationId);
        Assert.Equal(canonical.Json, after.Json);
    }

    [Fact]
    public async Task Live_mode_middleware_still_writes_a_route_key_row()
    {
        var publicationId = await PublishAsync("song-a");
        var built = Encoding.UTF8.GetBytes(
            """{"count":1,"currentSeason":14,"songs":[{"songId":"song-a"}]}""");

        await InvokeSongsMiddlewareAsync(
            publicationId,
            publicationBoundReads: false,
            built);

        Assert.Equal(1, CountRouteKeyRows());
    }

    private async Task<byte[]> InvokeSongsMiddlewareAsync(
        long publicationId,
        bool publicationBoundReads,
        byte[] responseBody)
    {
        var gate = new PublicReadGateService(
            Db,
            Microsoft.Extensions.Logging.Abstractions
                .NullLogger<PublicReadGateService>.Instance);
        var publicationCache = new PublicationApiResponseCacheService(
            Db,
            gate,
            () => publicationId,
            Microsoft.Extensions.Logging.Abstractions
                .NullLogger<PublicationApiResponseCacheService>.Instance);
        var songsCache = new SongsCacheService(
            gate,
            () => publicationId,
            publicationCache,
            publicationBoundReads);
        var middleware = new PublicApiResponseCacheMiddleware(
            async ctx =>
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.Body.WriteAsync(responseBody);
            },
            Microsoft.Extensions.Logging.Abstractions
                .NullLogger<PublicApiResponseCacheMiddleware>.Instance);
        var context = SongsRequestContext();
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();

        await middleware.InvokeAsync(
            context,
            Db,
            gate,
            new PublicApiCacheTelemetry(),
            publicationService: null,
            cacheService: publicationCache,
            songsCache: songsCache);
        context.Response.Body.Position = 0;
        using var served = new MemoryStream();
        await context.Response.Body.CopyToAsync(served);
        return served.ToArray();
    }

    private static DefaultHttpContext SongsRequestContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/songs";
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/api/songs"),
            0,
            new EndpointMetadataCollection(
                new HttpMethodMetadata([HttpMethods.Get]),
                PublicationBound.Instance),
            "/api/songs"));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private void Prime(
        SongsCacheService cache,
        FestivalService? service = null)
        => cache.Prime(
            service ?? CreateFestivalService(),
            CreateStore(cache.PublicationBoundReads),
            Db,
            CreateLeaderboardPersistence(),
            CreatePrecomputer(),
            new JsonSerializerOptions(),
            persistPublicationCache: true);

    private SongsCacheService CreateCache(
        bool publicationBoundReads,
        long publicationId)
        => CreateCache(publicationBoundReads, () => publicationId);

    private SongsCacheService CreateCache(
        bool publicationBoundReads,
        Func<long?> publicationIdProvider)
    {
        var gate = new PublicReadGateService(
            Db,
            Microsoft.Extensions.Logging.Abstractions
                .NullLogger<PublicReadGateService>.Instance);
        var publicationCache = new PublicationApiResponseCacheService(
            Db,
            gate,
            publicationIdProvider,
            Microsoft.Extensions.Logging.Abstractions
                .NullLogger<PublicationApiResponseCacheService>.Instance);
        return new SongsCacheService(
            gate,
            publicationIdProvider,
            publicationCache,
            publicationBoundReads);
    }

    private PublicationApiResponseCacheService CreatePublicationCache(
        long publicationId)
    {
        var gate = new PublicReadGateService(
            Db,
            Microsoft.Extensions.Logging.Abstractions
                .NullLogger<PublicReadGateService>.Instance);
        return new PublicationApiResponseCacheService(
            Db,
            gate,
            () => publicationId,
            Microsoft.Extensions.Logging.Abstractions
                .NullLogger<PublicationApiResponseCacheService>.Instance);
    }

    private async Task<long> PublishSecondAsync(params string[] songIds)
    {
        var persistence = new FestivalPersistence(DataSource);
        await persistence.SaveSongsVersionedAsync(
            songIds.Select(CreateCatalogSong).ToArray());
        var scrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(scrapeId, songIds.Length, 10, 1, 100);
        Db.PublishScrapeRun(scrapeId, promoteCachedResponses: false);
        return Db.GetPublicationPointerState().CurrentPublicationId!.Value;
    }

    /// <summary>
    /// Stores the degraded route-key payload a process-local endpoint build
    /// would have written before publication-bound ownership.
    /// </summary>
    private (byte[] Json, string ETag) StoreDegradedRouteKeyPayload(
        long publicationId)
    {
        var json = Encoding.UTF8.GetBytes(
            """{"count":1,"currentSeason":14,"songs":[{"songId":"song-a"}]}""");
        var etag = ResponseCacheService.ComputeETag(json);
        var routeCacheKey =
            PublicApiResponseCachePolicy.BuildCacheKey(
                SongsRequestContext().Request);
        Assert.StartsWith(
            PublicApiResponseCachePolicy.SongsRouteCacheKeyPrefix,
            routeCacheKey,
            StringComparison.Ordinal);
        StoreDurableRow(publicationId, routeCacheKey, json, etag);
        return (json, etag);
    }

    private int CountRouteKeyRows()
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM publication_api_response_cache
            WHERE cache_key LIKE @prefix || '%'
            """;
        command.Parameters.AddWithValue(
            "prefix",
            PublicApiResponseCachePolicy.SongsRouteCacheKeyPrefix);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private FestivalService CreateFestivalService(
        params string[] songIds)
        => FestivalService.CreateFromSongCatalogSnapshot(
            (songIds.Length == 0 ? ["song-a"] : songIds)
                .Select(CreateCatalogSong)
                .ToArray());

    private PathDataStore CreateStore(
        bool usePublicationArtifacts = false)
        => new(
            DataSource,
            null,
            Options.Create(new ScraperOptions
            {
                UsePublicationPathArtifacts =
                    usePublicationArtifacts,
            }));

    private GlobalLeaderboardPersistence CreateLeaderboardPersistence()
        => new(
            Db,
            Microsoft.Extensions.Logging.Abstractions
                .NullLoggerFactory.Instance,
            Microsoft.Extensions.Logging.Abstractions
                .NullLogger<GlobalLeaderboardPersistence>.Instance,
            DataSource,
            Options.Create(new FeatureOptions()));

    private ScrapeTimePrecomputer CreatePrecomputer()
        => new(
            CreateLeaderboardPersistence(),
            Db,
            CreateStore(),
            new ScrapeProgressTracker(),
            Microsoft.Extensions.Logging.Abstractions
                .NullLogger<ScrapeTimePrecomputer>.Instance,
            Microsoft.Extensions.Logging.Abstractions
                .NullLoggerFactory.Instance,
            new JsonSerializerOptions(),
            new FeatureOptions());

    private async Task<long> PublishAsync(params string[] songIds)
    {
        var persistence = new FestivalPersistence(DataSource);
        await persistence.SaveSongsVersionedAsync(
            songIds.Select(CreateCatalogSong).ToArray());
        var scrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(scrapeId, songIds.Length, 10, 1, 100);
        Db.PublishScrapeRun(scrapeId, promoteCachedResponses: false);
        return Db.GetPublicationPointerState().CurrentPublicationId!.Value;
    }

    /// <summary>
    /// Stores a canonical worker-built payload: the rich variant that includes
    /// population tiers an API-role precomputer cannot reproduce.
    /// </summary>
    private (byte[] Json, string ETag) StoreRichDurableSongsPayload(
        long publicationId)
    {
        var json = Encoding.UTF8.GetBytes(
            """
            {"count":1,"currentSeason":14,"songs":[{"songId":"song-a",
            "title":"song-a","populationTiers":{"Solo_Guitar":
            {"tier":1,"threshold":1234}}}]}
            """);
        var etag = ResponseCacheService.ComputeETag(json);
        StoreDurableRow(
            publicationId,
            PublicationApiCacheKeys.Songs,
            json,
            etag);
        return (json, etag);
    }

    private void StoreDurableRow(
        long publicationId,
        string cacheKey,
        byte[] json,
        string etag)
    {
        ExecuteNonQuery(
            """
            INSERT INTO publication_api_response_cache (
                publication_id, cache_key, json_data, etag, cached_at)
            VALUES (
                @publicationId, @cacheKey, @json, @etag, now())
            ON CONFLICT (publication_id, cache_key) DO UPDATE SET
                json_data = EXCLUDED.json_data,
                etag = EXCLUDED.etag,
                cached_at = EXCLUDED.cached_at
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "publicationId",
                    publicationId);
                command.Parameters.AddWithValue("cacheKey", cacheKey);
                command.Parameters.AddWithValue("json", json);
                command.Parameters.AddWithValue("etag", etag);
            });
    }

    private (byte[] Json, string ETag) ReadDurableSongs(long publicationId)
        => TryReadDurableSongs(publicationId)
           ?? throw new InvalidOperationException(
               "The durable songs cache row is missing.");

    private (byte[] Json, string ETag)? TryReadDurableSongs(
        long publicationId)
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT json_data, etag
            FROM publication_api_response_cache
            WHERE publication_id = @publicationId
              AND cache_key = @cacheKey
            """;
        command.Parameters.AddWithValue("publicationId", publicationId);
        command.Parameters.AddWithValue(
            "cacheKey",
            PublicationApiCacheKeys.Songs);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return (reader.GetFieldValue<byte[]>(0), reader.GetString(1));
    }

    private void ExecuteNonQuery(
        string sql,
        Action<NpgsqlCommand> configure)
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure(command);
        command.ExecuteNonQuery();
    }

    private static Song CreateCatalogSong(string songId) =>
        new()
        {
            _title = songId,
            lastModified = new DateTime(
                2026, 7, 31, 12, 0, 0, DateTimeKind.Utc),
            track = new Track
            {
                su = songId,
                tt = songId,
                an = "Artist",
                ab = "Album",
                au = $"https://example.test/{songId}.jpg",
                mu = $"https://example.test/{songId}.dat",
                sig = "4/4",
                ge = ["rock"],
                ry = 2026,
                mt = 120,
                dn = 200,
                @in = new In { gr = 1 },
            },
        };
}
