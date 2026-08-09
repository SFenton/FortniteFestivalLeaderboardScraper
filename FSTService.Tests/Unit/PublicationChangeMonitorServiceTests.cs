using System.Net.WebSockets;
using System.Reflection;
using FSTService.Api;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Npgsql;

namespace FSTService.Tests.Unit;

public sealed class PublicationChangeMonitorServiceTests
{
    [Fact]
    public async Task Monitor_PublicationChangeInvalidatesCachesAndRotatesSocket()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        var pointerReads = 0;
        metaDb.GetPublicationPointerState().Returns(_ =>
            Interlocked.Increment(ref pointerReads) == 1
                ? Pointer(41)
                : Pointer(42));
        metaDb.GetPublicReadFreezeState()
            .Returns(PublicReadFreezeState.NotFrozen);
        var notifications = new NotificationService(
            NullLogger<NotificationService>.Instance);
        var socket = Substitute.For<WebSocket>();
        socket.State.Returns(WebSocketState.Open);
        var rotated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        socket.CloseOutputAsync(
                Arg.Any<WebSocketCloseStatus>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                rotated.TrySetResult();
                return Task.CompletedTask;
            });
        notifications.AddConnection(
            "account",
            "device",
            socket,
            publicationId: 41);
        var caches = CreateCaches();
        var songsCache = new SongsCacheService();
        foreach (var cache in caches)
            cache.Set("cached", [1, 2, 3]);
        songsCache.Set([4, 5, 6]);
        var monitor = CreateMonitor(
            metaDb,
            notifications,
            songsCache,
            caches,
            NullLogger<PublicationChangeMonitorService>.Instance);

        try
        {
            await monitor.StartAsync(CancellationToken.None);
            Assert.Equal(1, Volatile.Read(ref pointerReads));
            Assert.All(caches, static cache =>
                Assert.Null(cache.Get("cached")));
            Assert.Null(songsCache.Get());

            foreach (var cache in caches)
                cache.Set("cached", [7, 8, 9]);
            songsCache.Set([10, 11, 12]);
            Assert.All(caches, static cache =>
                Assert.NotNull(cache.Get("cached")));
            Assert.NotNull(songsCache.Get());

            await rotated.Task.WaitAsync(TimeSpan.FromSeconds(4));
            await monitor.StopAsync(CancellationToken.None);

            Assert.True(pointerReads >= 2);
            Assert.All(caches, static cache =>
                Assert.Null(cache.Get("cached")));
            Assert.Null(songsCache.Get());
            await socket.Received(1).SendAsync(
                Arg.Is<ArraySegment<byte>>(segment =>
                    SegmentContains(
                        segment,
                        "\"type\":\"publication_changed\"",
                        "\"publicationId\":42")),
                WebSocketMessageType.Text,
                true,
                Arg.Any<CancellationToken>());
        }
        finally
        {
            foreach (var cache in caches)
                cache.Dispose();
        }
    }

    [Fact]
    public async Task Monitor_SamePublicationMaintenanceReleaseRotatesSocket()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicationPointerState().Returns(Pointer(51));
        var freezeReads = 0;
        metaDb.GetPublicReadFreezeState().Returns(_ =>
            Interlocked.Increment(ref freezeReads) == 1
                ? new PublicReadFreezeState(
                    true,
                    DateTime.UtcNow,
                    1277,
                    "path-repair-ranking-rebuild")
                : PublicReadFreezeState.NotFrozen);
        var notifications = new NotificationService(
            NullLogger<NotificationService>.Instance);
        var socket = Substitute.For<WebSocket>();
        socket.State.Returns(WebSocketState.Open);
        var rotated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        socket.CloseOutputAsync(
                Arg.Any<WebSocketCloseStatus>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                rotated.TrySetResult();
                return Task.CompletedTask;
            });
        notifications.AddConnection(
            "account",
            "device",
            socket,
            publicationId: 51);
        var caches = CreateCaches();
        var monitor = CreateMonitor(
            metaDb,
            notifications,
            new SongsCacheService(),
            caches,
            NullLogger<PublicationChangeMonitorService>.Instance);

        try
        {
            await monitor.StartAsync(CancellationToken.None);
            await rotated.Task.WaitAsync(TimeSpan.FromSeconds(4));
            await monitor.StopAsync(CancellationToken.None);

            Assert.True(freezeReads >= 2);
            await socket.Received(1).SendAsync(
                Arg.Is<ArraySegment<byte>>(segment =>
                    SegmentContains(
                        segment,
                        "\"type\":\"publication_changed\"",
                        "\"publicationId\":51")),
                WebSocketMessageType.Text,
                true,
                Arg.Any<CancellationToken>());
            await socket.Received(1).CloseOutputAsync(
                WebSocketCloseStatus.PolicyViolation,
                "Publication changed",
                Arg.Any<CancellationToken>());
        }
        finally
        {
            foreach (var cache in caches)
                cache.Dispose();
        }
    }

    [Fact]
    public async Task Monitor_ProbeFailureIsLoggedAndRetried()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicationPointerState()
            .Returns(_ => throw new InvalidOperationException("probe failed"));
        var logger = new TestLogger<PublicationChangeMonitorService>();
        var caches = CreateCaches();
        var monitor = CreateMonitor(
            metaDb,
            new NotificationService(
                NullLogger<NotificationService>.Instance),
            new SongsCacheService(),
            caches,
            logger);

        try
        {
            await monitor.StartAsync(CancellationToken.None);
            await monitor.StopAsync(CancellationToken.None);

            var entry = Assert.Single(
                logger.Entries,
                static item => item.Level == LogLevel.Warning);
            Assert.Contains(
                "Publication change monitor probe failed",
                entry.Message,
                StringComparison.Ordinal);
            Assert.IsType<InvalidOperationException>(entry.Exception);
        }
        finally
        {
            foreach (var cache in caches)
                cache.Dispose();
        }
    }

    private static PublicationChangeMonitorService CreateMonitor(
        IMetaDatabase metaDb,
        NotificationService notifications,
        SongsCacheService songsCache,
        ResponseCacheService[] caches,
        ILogger<PublicationChangeMonitorService> logger)
    {
        var startup = new StartupInitializer(
            null!,
            null!,
            null!,
            null!,
            null!,
            Options.Create(new ScraperOptions()),
            NullLogger<StartupInitializer>.Instance);
        var readySignal = Assert.IsType<TaskCompletionSource>(
            typeof(StartupInitializer)
                .GetField(
                    "_readySignal",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(startup));
        readySignal.TrySetResult();
        var publicReadGate = new PublicReadGateService(
            metaDb,
            NullLogger<PublicReadGateService>.Instance);
        var readContext = new PublicationReadContextService(
            metaDb,
            (NpgsqlDataSource)null!,
            Options.Create(new FeatureOptions()));
        var lifecycle = new ScrapeLifecycleNotifier(
            caches[0],
            caches[1],
            caches[2],
            caches[3],
            caches[4],
            metaDb,
            publicReadGate,
            readContext,
            NullLogger<ScrapeLifecycleNotifier>.Instance);
        return new PublicationChangeMonitorService(
            startup,
            metaDb,
            notifications,
            lifecycle,
            songsCache,
            logger);
    }

    private static ResponseCacheService[] CreateCaches() =>
    [
        new(TimeSpan.FromHours(1)),
        new(TimeSpan.FromHours(1)),
        new(TimeSpan.FromHours(1)),
        new(TimeSpan.FromHours(1)),
        new(TimeSpan.FromHours(1)),
    ];

    private static PublicationPointerState Pointer(long publicationId) =>
        new(
            CurrentPublicationId: publicationId,
            PreviousPublicationId: publicationId - 1,
            WorkingPublicationId: null,
            PublishedScrapeId: 1277,
            PublishedAtUtc: DateTime.UtcNow);

    private static bool SegmentContains(
        ArraySegment<byte> segment,
        params string[] snippets)
    {
        var text = System.Text.Encoding.UTF8.GetString(
            segment.Array!,
            segment.Offset,
            segment.Count);
        return snippets.All(text.Contains);
    }
}
