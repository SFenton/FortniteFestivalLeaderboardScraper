using System.Diagnostics;
using System.Text;
using FSTService.Api;
using FSTService.Persistence;
using FSTService.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace FSTService.Tests.Unit;

public sealed class PublicationApiCacheBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public PublicationApiCacheBenchmarkTests(
        ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Representative_songs_payload_meets_cold_and_warm_targets()
    {
        using var fixture = new InMemoryMetaDatabase();
        var metaDb = fixture.Db;
        var scrapeId = metaDb.StartScrapeRun();
        metaDb.BulkSetCachedResponses(
        [
            (
                Key: "seed",
                Json: new byte[] { 1 },
                ETag: "\"seed\""),
        ]);
        metaDb.CompleteScrapeRun(
            scrapeId,
            1,
            1,
            1,
            1);
        metaDb.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false);
        var publicationId =
            metaDb.GetPublicationPointerState()
                .CurrentPublicationId!.Value;
        var json = Encoding.UTF8.GetBytes(
            "{\"payload\":\""
            + new string('x', 722_980)
            + "\"}");
        Assert.NotNull(metaDb.TrySetCurrentCachedResponse(
            publicationId,
            PublicationApiCacheKeys.Songs,
            json,
            ResponseCacheService.ComputeETag(json)));
        var gate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var service = new PublicationApiResponseCacheService(
            metaDb,
            gate,
            () => publicationId,
            NullLogger<
                PublicationApiResponseCacheService>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/songs";
        Assert.True(
            PublicApiResponseCachePolicy
                .TryCreateRequestPlan(
                    context.Request,
                    out var plan));

        var cold = new List<double>();
        for (var index = 0; index < 30; index++)
        {
            service.InvalidateAll();
            var stopwatch = Stopwatch.StartNew();
            var hit = service.TryGetCurrent(plan);
            stopwatch.Stop();
            Assert.NotNull(hit);
            Assert.Equal(json.Length, hit.Value.Json.Length);
            cold.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        var warm = new List<double>();
        for (var index = 0; index < 1_000; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            var hit = service.TryGetCurrent(plan);
            stopwatch.Stop();
            Assert.NotNull(hit);
            warm.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        cold.Sort();
        warm.Sort();
        var coldP95 = Percentile95(cold);
        var warmP95 = Percentile95(warm);
        _output.WriteLine(
            "payloadBytes={0} coldP95Ms={1:F3} warmP95Ms={2:F3}",
            json.Length,
            coldP95,
            warmP95);
        Assert.True(
            coldP95 < 500,
            $"Cold L2 p95 {coldP95:F3}ms exceeded 500ms.");
        Assert.True(
            warmP95 < 10,
            $"Warm L1 p95 {warmP95:F3}ms exceeded 10ms.");
    }

    [Fact]
    public void Lazy_write_through_meets_hard_target_at_current_row_scale()
    {
        using var fixture = new InMemoryMetaDatabase();
        var metaDb = fixture.Db;
        var scrapeId = metaDb.StartScrapeRun();
        var seed = Enumerable.Range(0, 10_000)
            .Select(index => (
                Key: $"seed:{index:D5}",
                Json: new byte[] { 1, 2, 3, 4 },
                ETag: $"\"{index:D5}\""))
            .ToArray();
        metaDb.BulkSetCachedResponses(seed);
        metaDb.CompleteScrapeRun(
            scrapeId,
            1,
            1,
            1,
            1);
        metaDb.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false);
        var publicationId =
            metaDb.GetPublicationPointerState()
                .CurrentPublicationId!.Value;
        var durations = new List<double>();

        for (var index = 0; index < 10; index++)
        {
            var json = Encoding.UTF8.GetBytes(
                $"{{\"variant\":{index}}}");
            var stopwatch = Stopwatch.StartNew();
            var stored = metaDb.TrySetCurrentCachedResponse(
                publicationId,
                $"public-route:/api/rankings/overview?pageSize={25 + index}",
                json,
                ResponseCacheService.ComputeETag(json));
            stopwatch.Stop();
            Assert.NotNull(stored);
            durations.Add(
                stopwatch.Elapsed.TotalMilliseconds);
        }

        durations.Sort();
        var p50 = durations[durations.Count / 2];
        var p95 = Percentile95(durations);
        _output.WriteLine(
            "cacheRows=10000 writeP50Ms={0:F3} writeP95Ms={1:F3}",
            p50,
            p95);
        Assert.True(
            p95 < 500,
            $"Lazy write p95 {p95:F3}ms exceeded 500ms.");
    }

    private static double Percentile95(
        IReadOnlyList<double> sorted) =>
        sorted[Math.Max(
            0,
            (int)Math.Ceiling(sorted.Count * 0.95) - 1)];
}
