using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FSTService.Api;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FSTService.Tests.Unit;

/// <summary>
/// Tests for <see cref="NotificationService"/> — WebSocket connection management
/// and push notifications.
/// </summary>
public sealed class NotificationServiceTests
{
    private readonly ILogger<NotificationService> _log = Substitute.For<ILogger<NotificationService>>();

    private NotificationService CreateService() => new(_log);

    private static bool SegmentContains(ArraySegment<byte> segment, params string[] snippets)
    {
        var text = Encoding.UTF8.GetString(segment.Array!, segment.Offset, segment.Count);
        return snippets.All(text.Contains);
    }

    // ─── AddConnection / RemoveConnection ───────────────────────

    [Fact]
    public async Task AddConnection_ThenNotify_SendsMessage()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);

        svc.AddConnection("acct1", "dev1", ws);

        await svc.NotifyAccountAsync("acct1", new { type = "test" });

        await ws.Received(1).SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            WebSocketMessageType.Text,
            true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifyAccount_PublicationChanged_SendsControlMessageAndCloses()
    {
        var svc = CreateService();
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicationPointerState().Returns(
            new PublicationPointerState(43, 42, null, 1272, DateTime.UtcNow));
        svc.SetMetaDatabase(metaDb);
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);
        svc.AddConnection("acct1", "dev1", ws, publicationId: 42);

        await svc.NotifyAccountAsync("acct1", new { type = "scores_changed" });

        await ws.Received(1).SendAsync(
            Arg.Is<ArraySegment<byte>>(segment =>
                SegmentContains(
                    segment,
                    "\"type\":\"publication_changed\"",
                    "\"publicationId\":43")),
            WebSocketMessageType.Text,
            true,
            Arg.Any<CancellationToken>());
        await ws.Received(1).CloseOutputAsync(
            WebSocketCloseStatus.PolicyViolation,
            "Publication changed",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifyPublicationChanged_RotatesOnlyStaleConnections()
    {
        var svc = CreateService();
        var stale = Substitute.For<WebSocket>();
        var current = Substitute.For<WebSocket>();
        stale.State.Returns(WebSocketState.Open);
        current.State.Returns(WebSocketState.Open);
        svc.AddConnection("acct1", "stale", stale, publicationId: 41);
        svc.AddConnection("acct1", "current", current, publicationId: 42);

        await svc.NotifyPublicationChangedAsync(42);

        await stale.Received(1).SendAsync(
            Arg.Is<ArraySegment<byte>>(segment =>
                SegmentContains(
                    segment,
                    "\"type\":\"publication_changed\"",
                    "\"publicationId\":42")),
            WebSocketMessageType.Text,
            true,
            Arg.Any<CancellationToken>());
        await stale.Received(1).CloseOutputAsync(
            WebSocketCloseStatus.PolicyViolation,
            "Publication changed",
            Arg.Any<CancellationToken>());
        await current.DidNotReceive().SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            Arg.Any<WebSocketMessageType>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());

        stale.ClearReceivedCalls();
        current.ClearReceivedCalls();
        await svc.NotifyAccountAsync("acct1", new { type = "still_current" });

        await stale.DidNotReceive().SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            Arg.Any<WebSocketMessageType>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
        await current.Received(1).SendAsync(
            Arg.Is<ArraySegment<byte>>(segment =>
                SegmentContains(segment, "\"type\":\"still_current\"")),
            WebSocketMessageType.Text,
            true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifyPublicationChanged_NullPublicationIdentityFailsClosed()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);
        svc.AddConnection(
            "acct1",
            "unidentified",
            ws,
            publicationId: null);

        await svc.NotifyPublicationChangedAsync(42);

        await ws.Received(1).SendAsync(
            Arg.Is<ArraySegment<byte>>(segment =>
                SegmentContains(
                    segment,
                    "\"type\":\"publication_changed\"",
                    "\"publicationId\":42")),
            WebSocketMessageType.Text,
            true,
            Arg.Any<CancellationToken>());
        await ws.Received(1).CloseOutputAsync(
            WebSocketCloseStatus.PolicyViolation,
            "Publication changed",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifyPublicationChanged_ForceRefreshRotatesCurrentConnections()
    {
        var svc = CreateService();
        var current = Substitute.For<WebSocket>();
        current.State.Returns(WebSocketState.Open);
        svc.AddConnection("acct1", "current", current, publicationId: 42);

        await svc.NotifyPublicationChangedAsync(
            42,
            forceRefresh: true);

        await current.Received(1).SendAsync(
            Arg.Is<ArraySegment<byte>>(segment =>
                SegmentContains(
                    segment,
                    "\"type\":\"publication_changed\"",
                    "\"publicationId\":42")),
            WebSocketMessageType.Text,
            true,
            Arg.Any<CancellationToken>());
        await current.Received(1).CloseOutputAsync(
            WebSocketCloseStatus.PolicyViolation,
            "Publication changed",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifyPublicationChanged_SendFailureRemovesConnection()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);
        ws.SendAsync(
                Arg.Any<ArraySegment<byte>>(),
                Arg.Any<WebSocketMessageType>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new WebSocketException("publication rotation failed"));
        svc.AddConnection("acct1", "dev1", ws, publicationId: 41);

        await svc.NotifyPublicationChangedAsync(42);

        ws.ClearReceivedCalls();
        await svc.NotifyAccountAsync("acct1", new { type = "after_failure" });
        await ws.DidNotReceive().SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            Arg.Any<WebSocketMessageType>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnpinnedPublishedSourceWebSocketClosesWhenPublicationChanges()
    {
        using var fixture = new Helpers.InMemoryMetaDatabase();
        var scrapeId = fixture.Db.StartScrapeRun();
        fixture.Db.CompleteScrapeRun(
            scrapeId,
            songsScraped: 1,
            totalEntries: 1,
            totalRequests: 1,
            totalBytes: 1);
        fixture.Db.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false);
        var storedPointers =
            fixture.Db.GetPublicationPointerState();
        var metaDb = Substitute.For<IMetaDatabase>();
        var currentPublicationId =
            Assert.IsType<long>(
                storedPointers.CurrentPublicationId);
        var currentPublishedScrapeId =
            Assert.IsType<long>(
                storedPointers.PublishedScrapeId);
        metaDb.GetPublicationPointerState().Returns(_ =>
            new PublicationPointerState(
                currentPublicationId,
                storedPointers.PreviousPublicationId,
                WorkingPublicationId: null,
                currentPublishedScrapeId,
                PublishedAtUtc: DateTime.UtcNow));
        metaDb.GetPublicationPointerState(
                Arg.Any<int>())
            .Returns(_ =>
                new PublicationPointerState(
                    currentPublicationId,
                    storedPointers.PreviousPublicationId,
                    WorkingPublicationId: null,
                    currentPublishedScrapeId,
                    PublishedAtUtc: DateTime.UtcNow));
        metaDb.GetPublicationSurfaceSourceEvidence(
                currentPublicationId,
                PublicationSurfaceNames.SoloScopeSources,
                Arg.Any<int>())
            .Returns(new PublicationSurfaceSourceEvidence(
                PublicationSurfaceNames.SoloScopeSources,
                Exists: true,
                currentPublicationId,
                currentPublishedScrapeId,
                RowCount: 1,
                ContentHash: new string('a', 64)));
        var publicationService =
            new PublicationReadContextService(
                metaDb,
                fixture.DataSource,
                Options.Create(new FeatureOptions
                {
                    EnablePublicationReadContext = false,
                    UsePublishedScopeSources = true,
                }));
        Assert.False(publicationService.PinningConfigured);
        var svc = CreateService();
        svc.SetMetaDatabase(metaDb);
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);
        var receiveStarted =
            new TaskCompletionSource(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        ws.ReceiveAsync(
                Arg.Any<ArraySegment<byte>>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                receiveStarted.TrySetResult();
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    call.ArgAt<CancellationToken>(1));
                return new WebSocketReceiveResult(
                    0,
                    WebSocketMessageType.Close,
                    true);
            });
        using var cancellation =
            new CancellationTokenSource();
        var boundary =
            new PublicationBoundaryReadLeaseMiddleware(
                context => svc.HandleConnectionAsync(
                    "acct1",
                    "dev1",
                    ws,
                    context.GetPublicationReadContext()
                        ?.PublicationId,
                    publicationService,
                    cancellation.Token));
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/ws";
        context.RequestAborted = cancellation.Token;
        var webSocketFeature =
            Substitute.For<IHttpWebSocketFeature>();
        webSocketFeature.IsWebSocketRequest.Returns(true);
        context.Features.Set(webSocketFeature);
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/api/ws"),
            0,
            new EndpointMetadataCollection(
                PublicationBound.Instance,
                new HttpMethodMetadata([HttpMethods.Get])),
            "/api/ws"));

        var connectionTask = boundary.InvokeAsync(
            context,
            publicationService,
            new PublicReadGateService(
                metaDb,
                Microsoft.Extensions.Logging.Abstractions
                    .NullLogger<PublicReadGateService>.Instance),
            Substitute.For<IPathDataStore>());
        await receiveStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        currentPublicationId++;

        await svc.NotifyPublicationChangedAsync(
            currentPublicationId);

        await ws.Received(1).SendAsync(
            Arg.Is<ArraySegment<byte>>(segment =>
                SegmentContains(
                    segment,
                    "\"type\":\"publication_changed\"",
                    $"\"publicationId\":{currentPublicationId}")),
            WebSocketMessageType.Text,
            true,
            Arg.Any<CancellationToken>());
        await ws.Received(1).CloseOutputAsync(
            WebSocketCloseStatus.PolicyViolation,
            "Publication changed",
            Arg.Any<CancellationToken>());
        cancellation.Cancel();
        await connectionTask;
    }

    [Fact]
    public async Task SourceOnlyWebSocketAdmissionSerializesRegistrationWithPublicationCommit()
    {
        using var fixture = new Helpers.InMemoryMetaDatabase();
        var scrapeId = fixture.Db.StartScrapeRun();
        fixture.Db.CompleteScrapeRun(
            scrapeId,
            songsScraped: 1,
            totalEntries: 1,
            totalRequests: 1,
            totalBytes: 1);
        fixture.Db.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false);
        var pointers = fixture.Db.GetPublicationPointerState();
        var publicationId =
            Assert.IsType<long>(pointers.CurrentPublicationId);
        var publishedScrapeId =
            Assert.IsType<long>(pointers.PublishedScrapeId);

        var validationEntered =
            new TaskCompletionSource(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        var releaseValidation =
            new TaskCompletionSource(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicationPointerState(
                Arg.Any<int>())
            .Returns(pointers);
        metaDb.GetPublicationSurfaceSourceEvidence(
                publicationId,
                PublicationSurfaceNames.SoloScopeSources,
                Arg.Any<int>())
            .Returns(_ =>
            {
                validationEntered.TrySetResult();
                if (!releaseValidation.Task
                        .Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException(
                        "Timed out waiting to release WebSocket admission validation.");
                }
                return new PublicationSurfaceSourceEvidence(
                    PublicationSurfaceNames.SoloScopeSources,
                    Exists: true,
                    publicationId,
                    publishedScrapeId,
                    RowCount: 1,
                    ContentHash: new string('a', 64));
            });
        var publicationService =
            new PublicationReadContextService(
                metaDb,
                fixture.DataSource,
                Options.Create(new FeatureOptions
                {
                    EnablePublicationReadContext = false,
                    UsePublishedScopeSources = true,
                }));
        var notifications = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);
        var receiveStarted =
            new TaskCompletionSource(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        ws.ReceiveAsync(
                Arg.Any<ArraySegment<byte>>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                receiveStarted.TrySetResult();
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    call.ArgAt<CancellationToken>(1));
                return new WebSocketReceiveResult(
                    0,
                    WebSocketMessageType.Close,
                    true);
            });
        using var cancellation =
            new CancellationTokenSource();
        var connectionTask = Task.Run(
            () => notifications.HandleConnectionAsync(
                "acct1",
                "dev1",
                ws,
                publicationId,
                publicationService,
                cancellation.Token));
        await validationEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        var commitLockAcquired =
            new TaskCompletionSource(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        var releaseCommit =
            new TaskCompletionSource(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        var commitTask = Task.Run(async () =>
        {
            await using var connection =
                await fixture.DataSource
                    .OpenConnectionAsync();
            await using var transaction =
                await connection.BeginTransactionAsync();
            await using var command =
                connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "SELECT pg_advisory_xact_lock(@lockKey)";
            command.Parameters.AddWithValue(
                "lockKey",
                PublicationGenerationSchema
                    .AdvisoryLockKey);
            await command.ExecuteNonQueryAsync();
            commitLockAcquired.TrySetResult();
            await releaseCommit.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            await transaction.CommitAsync();
        });

        var earlyCommitLock =
            await Task.WhenAny(
                commitLockAcquired.Task,
                Task.Delay(TimeSpan.FromMilliseconds(250)));
        var commitPassedAdmission =
            ReferenceEquals(
                earlyCommitLock,
                commitLockAcquired.Task);

        releaseValidation.TrySetResult();
        await receiveStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        await commitLockAcquired.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        releaseCommit.TrySetResult();
        await commitTask;

        await notifications.NotifyPublicationChangedAsync(
            publicationId + 1);

        await ws.Received(1).SendAsync(
            Arg.Is<ArraySegment<byte>>(segment =>
                SegmentContains(
                    segment,
                    "\"type\":\"publication_changed\"",
                    $"\"publicationId\":{publicationId + 1}")),
            WebSocketMessageType.Text,
            true,
            Arg.Any<CancellationToken>());
        await ws.Received(1).CloseOutputAsync(
            WebSocketCloseStatus.PolicyViolation,
            "Publication changed",
            Arg.Any<CancellationToken>());
        cancellation.Cancel();
        await connectionTask;

        Assert.False(
            commitPassedAdmission,
            "Publication commit acquired its exclusive lock while source-only WebSocket validation had not yet registered the socket.");
    }

    [Fact]
    public async Task SourceOnlyWebSocketAdmissionPropagatesCancellationWhileCommitOwnsLock()
    {
        using var fixture = new Helpers.InMemoryMetaDatabase();
        var scrapeId = fixture.Db.StartScrapeRun();
        fixture.Db.CompleteScrapeRun(
            scrapeId,
            songsScraped: 1,
            totalEntries: 1,
            totalRequests: 1,
            totalBytes: 1);
        fixture.Db.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false);
        var pointers =
            fixture.Db.GetPublicationPointerState();
        var publicationId =
            Assert.IsType<long>(
                pointers.CurrentPublicationId);
        var publishedScrapeId =
            Assert.IsType<long>(
                pointers.PublishedScrapeId);
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicationPointerState(
                Arg.Any<int>())
            .Returns(pointers);
        metaDb.GetPublicationSurfaceSourceEvidence(
                publicationId,
                PublicationSurfaceNames.SoloScopeSources,
                Arg.Any<int>())
            .Returns(new PublicationSurfaceSourceEvidence(
                PublicationSurfaceNames.SoloScopeSources,
                Exists: true,
                publicationId,
                publishedScrapeId,
                RowCount: 1,
                ContentHash: new string('a', 64)));
        var publicationService =
            new PublicationReadContextService(
                metaDb,
                fixture.DataSource,
                Options.Create(new FeatureOptions
                {
                    EnablePublicationReadContext = false,
                    UsePublishedScopeSources = true,
                }));
        var notifications = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);

        await using var lockConnection =
            await fixture.DataSource.OpenConnectionAsync();
        await using var lockTransaction =
            await lockConnection.BeginTransactionAsync();
        await using (var lockCommand =
                     lockConnection.CreateCommand())
        {
            lockCommand.Transaction = lockTransaction;
            lockCommand.CommandText =
                "SELECT pg_advisory_xact_lock(@lockKey)";
            lockCommand.Parameters.AddWithValue(
                "lockKey",
                PublicationGenerationSchema
                    .AdvisoryLockKey);
            await lockCommand.ExecuteNonQueryAsync();
        }
        using var cancellation =
            new CancellationTokenSource(
                TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => notifications.HandleConnectionAsync(
                "acct1",
                "dev1",
                ws,
                publicationId,
                publicationService,
                cancellation.Token));

        await ws.DidNotReceive().ReceiveAsync(
            Arg.Any<ArraySegment<byte>>(),
            Arg.Any<CancellationToken>());
        await lockTransaction.RollbackAsync();
    }

    [Fact]
    public async Task SourceOnlyWebSocketAdmissionWaitsForCommitThenRejectsStalePublication()
    {
        using var fixture = new Helpers.InMemoryMetaDatabase();
        var firstScrapeId = fixture.Db.StartScrapeRun();
        fixture.Db.CompleteScrapeRun(
            firstScrapeId,
            songsScraped: 1,
            totalEntries: 1,
            totalRequests: 1,
            totalBytes: 1);
        fixture.Db.PublishScrapeRun(
            firstScrapeId,
            promoteCachedResponses: false);
        var stalePublicationId =
            Assert.IsType<long>(
                fixture.Db.GetPublicationPointerState()
                    .CurrentPublicationId);
        var secondScrapeId = fixture.Db.StartScrapeRun();
        fixture.Db.CompleteScrapeRun(
            secondScrapeId,
            songsScraped: 1,
            totalEntries: 1,
            totalRequests: 1,
            totalBytes: 1);
        fixture.Db.PublishScrapeRun(
            secondScrapeId,
            promoteCachedResponses: false);
        var currentPointers =
            fixture.Db.GetPublicationPointerState();
        var currentPublicationId =
            Assert.IsType<long>(
                currentPointers.CurrentPublicationId);

        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicationPointerState(
                Arg.Any<int>())
            .Returns(currentPointers);
        var publicationService =
            new PublicationReadContextService(
                metaDb,
                fixture.DataSource,
                Options.Create(new FeatureOptions
                {
                    EnablePublicationReadContext = false,
                    UsePublishedScopeSources = true,
                }));
        var notifications = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);

        await using var lockConnection =
            await fixture.DataSource.OpenConnectionAsync();
        await using var lockTransaction =
            await lockConnection.BeginTransactionAsync();
        await using (var lockCommand =
                     lockConnection.CreateCommand())
        {
            lockCommand.Transaction = lockTransaction;
            lockCommand.CommandText =
                "SELECT pg_advisory_xact_lock(@lockKey)";
            lockCommand.Parameters.AddWithValue(
                "lockKey",
                PublicationGenerationSchema
                    .AdvisoryLockKey);
            await lockCommand.ExecuteNonQueryAsync();
        }

        var admissionTask = Task.Run(
            () => notifications.HandleConnectionAsync(
                "acct1",
                "dev1",
                ws,
                stalePublicationId,
                publicationService,
                CancellationToken.None));
        await Task.Delay(
            TimeSpan.FromMilliseconds(250));

        await ws.DidNotReceive().SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            Arg.Any<WebSocketMessageType>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
        await ws.DidNotReceive().CloseOutputAsync(
            Arg.Any<WebSocketCloseStatus>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());

        await lockTransaction.RollbackAsync();
        await admissionTask.WaitAsync(
            TimeSpan.FromSeconds(5));

        await ws.Received(1).SendAsync(
            Arg.Is<ArraySegment<byte>>(segment =>
                SegmentContains(
                    segment,
                    "\"type\":\"publication_changed\"",
                    $"\"publicationId\":{currentPublicationId}")),
            WebSocketMessageType.Text,
            true,
            Arg.Any<CancellationToken>());
        await ws.Received(1).CloseOutputAsync(
            WebSocketCloseStatus.PolicyViolation,
            "Publication changed",
            Arg.Any<CancellationToken>());
        await ws.DidNotReceive().ReceiveAsync(
            Arg.Any<ArraySegment<byte>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public Task SourceOnlyWebSocketSubscribeRebindSerializesWithPublicationCommit() =>
        AssertSourceOnlyWebSocketRebindSerializesWithPublicationCommitAsync(
            unsubscribe: false);

    [Fact]
    public Task SourceOnlyWebSocketUnsubscribeRebindSerializesWithPublicationCommit() =>
        AssertSourceOnlyWebSocketRebindSerializesWithPublicationCommitAsync(
            unsubscribe: true);

    [Fact]
    public async Task RemoveConnection_ThenNotify_DoesNotSend()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);

        svc.AddConnection("acct1", "dev1", ws);
        svc.RemoveConnection("acct1", "dev1");

        await svc.NotifyAccountAsync("acct1", new { type = "test" });

        await ws.DidNotReceive().SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            Arg.Any<WebSocketMessageType>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveConnection_StaleSocket_DoesNotRemoveReplacement()
    {
        var svc = CreateService();
        var staleWs = Substitute.For<WebSocket>();
        var replacementWs = Substitute.For<WebSocket>();
        staleWs.State.Returns(WebSocketState.Open);
        replacementWs.State.Returns(WebSocketState.Open);

        svc.AddConnection("acct1", "dev1", staleWs);
        svc.AddConnection("acct1", "dev1", replacementWs);

        svc.RemoveConnection("acct1", "dev1", staleWs);
        await svc.NotifyAccountAsync("acct1", new { type = "test" });

        await staleWs.DidNotReceive().SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            Arg.Any<WebSocketMessageType>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
        await replacementWs.Received(1).SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            WebSocketMessageType.Text,
            true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void RemoveConnection_UnknownAccount_DoesNotThrow()
    {
        var svc = CreateService();
        // Should not throw
        svc.RemoveConnection("unknown", "unknown");
    }

    // ─── NotifyAccountAsync ─────────────────────────────────────

    [Fact]
    public async Task NotifyAccountAsync_UnknownAccount_DoesNothing()
    {
        var svc = CreateService();
        // Should not throw
        await svc.NotifyAccountAsync("nobody", new { type = "test" });
    }

    [Fact]
    public async Task NotifyAccountAsync_ClosedSocket_CleanedUp()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Closed);

        svc.AddConnection("acct1", "dev1", ws);

        await svc.NotifyAccountAsync("acct1", new { type = "test" });

        // Should NOT have tried to send (socket is closed)
        await ws.DidNotReceive().SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            Arg.Any<WebSocketMessageType>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());

        // After cleanup, notifying again should do nothing (no crash)
        await svc.NotifyAccountAsync("acct1", new { type = "test2" });
    }

    [Fact]
    public async Task NotifyAccountAsync_SendThrows_CleansUpDeadConnection()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);
        ws.When(x => x.SendAsync(
                Arg.Any<ArraySegment<byte>>(),
                Arg.Any<WebSocketMessageType>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>()))
            .Do(_ => throw new WebSocketException("Connection lost"));

        svc.AddConnection("acct1", "dev1", ws);

        // Should not throw — exception is caught and connection cleaned up
        await svc.NotifyAccountAsync("acct1", new { type = "test" });

        // Sending again should not try to send (connection was removed)
        ws.ClearReceivedCalls();
        await svc.NotifyAccountAsync("acct1", new { type = "test2" });
        await ws.DidNotReceive().SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            Arg.Any<WebSocketMessageType>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifyAccountAsync_MultipleDevices_AllReceive()
    {
        var svc = CreateService();
        var ws1 = Substitute.For<WebSocket>();
        var ws2 = Substitute.For<WebSocket>();
        ws1.State.Returns(WebSocketState.Open);
        ws2.State.Returns(WebSocketState.Open);

        svc.AddConnection("acct1", "dev1", ws1);
        svc.AddConnection("acct1", "dev2", ws2);

        await svc.NotifyAccountAsync("acct1", new { type = "broadcast" });

        await ws1.Received(1).SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
        await ws2.Received(1).SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }

    // ─── Convenience methods ────────────────────────────────────

    [Fact]
    public async Task NotifyBackfillCompleteAsync_SendsCorrectType()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);

        svc.AddConnection("acct1", "dev1", ws);

        await svc.NotifyBackfillCompleteAsync("acct1");

        await ws.Received(1).SendAsync(
            Arg.Is<ArraySegment<byte>>(seg =>
                Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count).Contains("backfill_complete")),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifyHistoryReconCompleteAsync_SendsCorrectType()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);

        svc.AddConnection("acct1", "dev1", ws);

        await svc.NotifyHistoryReconCompleteAsync("acct1");

        await ws.Received(1).SendAsync(
            Arg.Is<ArraySegment<byte>>(seg =>
                Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count).Contains("history_recon_complete")),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }

    // ─── HandleConnectionAsync ──────────────────────────────

    [Fact]
    public async Task HandleConnectionAsync_ClientSendsClose_ClosesGracefully()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();

        // First call: Open and return Close message. Second call: state changes to Closed.
        int callCount = 0;
        ws.State.Returns(_ => callCount < 1 ? WebSocketState.Open : WebSocketState.Closed);
        ws.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            });

        await svc.HandleConnectionAsync("acct1", "dev1", ws, CancellationToken.None);

        await ws.Received(1).CloseOutputAsync(
            WebSocketCloseStatus.NormalClosure, "Goodbye", CancellationToken.None);
    }

    [Fact]
    public async Task HandleConnectionAsync_WebSocketException_BreaksLoop()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);
        ws.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new WebSocketException("Connection reset"));

        // Should complete without throwing
        await svc.HandleConnectionAsync("acct1", "dev1", ws, CancellationToken.None);

        // Connection should be removed in the finally block
        // Verify by sending a notification — no send should occur
        await svc.NotifyAccountAsync("acct1", new { type = "test" });
        await ws.DidNotReceive().SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleConnectionAsync_Cancellation_BreaksLoop()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);

        var cts = new CancellationTokenSource();
        ws.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await svc.HandleConnectionAsync("acct1", "dev1", ws, cts.Token);

        // Connection should be cleaned up
        await svc.NotifyAccountAsync("acct1", new { type = "test" });
        await ws.DidNotReceive().SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleConnectionAsync_TextMessage_ContinuesReading()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();

        int callCount = 0;
        ws.State.Returns(_ => callCount < 2 ? WebSocketState.Open : WebSocketState.Closed);
        ws.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 1)
                    return new WebSocketReceiveResult(5, WebSocketMessageType.Text, true);
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            });

        await svc.HandleConnectionAsync("acct1", "dev1", ws, CancellationToken.None);

        // Should have received text then close
        await ws.Received(2).ReceiveAsync(
            Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleConnectionAsync_ConfiguredUnreadyPublication_ClosesWithoutRegistering()
    {
        using var fixture = new Helpers.InMemoryMetaDatabase();
        var metaDb = Substitute.For<IMetaDatabase>();
        var now = DateTime.UtcNow;
        var pointers = new PublicationPointerState(
            CurrentPublicationId: 42,
            PreviousPublicationId: 41,
            WorkingPublicationId: null,
            PublishedScrapeId: 1277,
            PublishedAtUtc: now);
        metaDb.GetPublicationPointerState().Returns(pointers);
        metaDb.GetPublicationGeneration(42).Returns(
            new PublicationGenerationInfo(
                42,
                1277,
                PublicationGenerationStatus.Current,
                41,
                now.AddMinutes(-5),
                now.AddMinutes(-4),
                now.AddMinutes(-2),
                now,
                null,
                null,
                null));
        metaDb.GetPublicationSurfaceBindings(42).Returns([]);
        var publicationService = new PublicationReadContextService(
            metaDb,
            fixture.DataSource,
            Options.Create(new FeatureOptions
            {
                EnablePublicationReadContext = true,
            }));
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);

        await svc.HandleConnectionAsync(
            "acct1",
            "dev1",
            ws,
            publicationId: 42,
            publicationService,
            CancellationToken.None);

        await ws.Received(1).CloseOutputAsync(
            WebSocketCloseStatus.PolicyViolation,
            "Publication unavailable",
            CancellationToken.None);
        await ws.DidNotReceive().ReceiveAsync(
            Arg.Any<ArraySegment<byte>>(),
            Arg.Any<CancellationToken>());
    }

    // ─── BroadcastAllAsync ──────────────────────────────────

    [Fact]
    public async Task BroadcastAllAsync_NoClients_DoesNotThrow()
    {
        var svc = CreateService();
        await svc.BroadcastAllAsync(new { type = "test" });
    }

    [Fact]
    public async Task BroadcastAllAsync_OpenClient_SendsMessage()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);
        svc.AddConnection("acct1", "dev1", ws);

        await svc.BroadcastAllAsync(new { type = "shop_changed" });

        await ws.Received(1).SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BroadcastAllAsync_ClosedClient_CleanedUp()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Closed);
        svc.AddConnection("acct1", "dev1", ws);

        await svc.BroadcastAllAsync(new { type = "test" });

        // Send should not have been called (closed socket)
        await ws.DidNotReceive().SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BroadcastAllAsync_SendThrows_RemovesDeadConnection()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);
        ws.SendAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<WebSocketMessageType>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new WebSocketException("broken"));
        svc.AddConnection("acct1", "dev1", ws);

        await svc.BroadcastAllAsync(new { type = "test" });

        // After cleanup, notify should not attempt to send again
        ws.ClearReceivedCalls();
        await svc.NotifyAccountAsync("acct1", new { type = "check" });
        await ws.DidNotReceive().SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            Arg.Any<WebSocketMessageType>(), Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    // ─── SendShopSnapshotAsync ──────────────────────────────

    [Fact]
    public async Task SendShopSnapshotAsync_OpenSocket_Sends()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);

        await svc.SendShopSnapshotAsync(ws, new[] { "song1" }, Array.Empty<string>(), new[] { "song1" });

        await ws.Received(1).SendAsync(
            Arg.Is<ArraySegment<byte>>(seg =>
                Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count).Contains("shop_snapshot") &&
                Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count).Contains("newSongs")),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendShopSnapshotAsync_ClosedSocket_DoesNotSend()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Closed);

        await svc.SendShopSnapshotAsync(ws, new[] { "song1" }, Array.Empty<string>(), Array.Empty<string>());

        await ws.DidNotReceive().SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            Arg.Any<WebSocketMessageType>(), Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    // ─── NotifyShopChangedAsync ─────────────────────────────

    [Fact]
    public async Task NotifyShopChangedAsync_BroadcastsToAll()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);
        svc.AddConnection("acct1", "dev1", ws);

        await svc.NotifyShopChangedAsync(
            new[] { "added1" }, new[] { "removed1" }, 5, new[] { "leaving1" }, new[] { "added1" });

        await ws.Received(1).SendAsync(
            Arg.Is<ArraySegment<byte>>(seg =>
                Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count).Contains("shop_changed") &&
                Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count).Contains("newSongs")),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifySongsChangedAsync_PreservesTotalAndAddsPublicationLag()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);
        svc.AddConnection("acct1", "dev1", ws);

        await svc.NotifySongsChangedAsync(
            total: 710,
            added: 3,
            removed: 1,
            changed: 2,
            publishedTotal: 707,
            awaitingPublication: 6);

        await ws.Received(1).SendAsync(
            Arg.Is<ArraySegment<byte>>(
                segment =>
                    ContainsCatalogLagMessage(segment)),
            WebSocketMessageType.Text,
            true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifySongsChangedAsync_OmitsUnavailableLagFields()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);
        svc.AddConnection("acct1", "dev1", ws);

        await svc.NotifySongsChangedAsync(
            total: 710,
            added: 3,
            removed: 0,
            changed: 0,
            publishedTotal: null,
            awaitingPublication: null);

        await ws.Received(1).SendAsync(
            Arg.Is<ArraySegment<byte>>(segment =>
                ContainsSongsChangedWithoutLag(segment)),
            WebSocketMessageType.Text,
            true,
            Arg.Any<CancellationToken>());
    }

    private static bool ContainsSongsChangedWithoutLag(
        ArraySegment<byte> segment)
    {
        using var document = JsonDocument.Parse(
            Encoding.UTF8.GetString(
                segment.Array!,
                segment.Offset,
                segment.Count));
        var root = document.RootElement;
        return root.GetProperty("type").GetString()
                == "songs_changed"
            && root.GetProperty("total").GetInt32() == 710
            && !root.TryGetProperty(
                "publishedTotal",
                out _)
            && !root.TryGetProperty(
                "awaitingPublication",
                out _);
    }

    private static bool ContainsCatalogLagMessage(
        ArraySegment<byte> segment)
    {
        using var document = JsonDocument.Parse(
            Encoding.UTF8.GetString(
                segment.Array!,
                segment.Offset,
                segment.Count));
        var root = document.RootElement;
        return root.GetProperty("type").GetString()
                == "songs_changed"
            && root.GetProperty("total").GetInt32() == 710
            && root.GetProperty("added").GetInt32() == 3
            && root.GetProperty("removed").GetInt32() == 1
            && root.GetProperty("changed").GetInt32() == 2
            && root.GetProperty("publishedTotal").GetInt32()
                == 707
            && root.GetProperty("awaitingPublication")
                .GetInt32() == 6;
    }

    // ─── HandleConnectionAsync with ShopProvider ────────────

    [Fact]
    public async Task HandleConnectionAsync_WithShopProvider_SendsSnapshotOnConnect()
    {
        var svc = CreateService();
        var shopProvider = Substitute.For<IShopProvider>();
        shopProvider.InShopSongIds.Returns(new HashSet<string> { "shop_s1" });
        shopProvider.LeavingTomorrowSongIds.Returns(new HashSet<string> { "leaving_s1" });
        shopProvider.NewSongIds.Returns(new HashSet<string> { "shop_s1" });
        svc.SetShopProvider(shopProvider);

        // FestivalService is needed to enrich shop snapshots — use a real one (empty songs is fine)
        var festivalService = new FortniteFestival.Core.Services.FestivalService();
        svc.SetFestivalService(festivalService);

        var ws = Substitute.For<WebSocket>();
        int callCount = 0;
        ws.State.Returns(_ => callCount < 1 ? WebSocketState.Open : WebSocketState.Closed);
        ws.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            });

        await svc.HandleConnectionAsync("acct1", "dev1", ws, CancellationToken.None);

        // Snapshot should have been sent
        await ws.Received().SendAsync(
            Arg.Is<ArraySegment<byte>>(seg =>
                Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count).Contains("shop_snapshot")),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleConnectionAsync_ShopProviderThrows_DoesNotCrash()
    {
        var svc = CreateService();
        var shopProvider = Substitute.For<IShopProvider>();
        shopProvider.InShopSongIds.Returns(_ => throw new InvalidOperationException("Shop not ready"));
        svc.SetShopProvider(shopProvider);

        var ws = Substitute.For<WebSocket>();
        int callCount = 0;
        ws.State.Returns(_ => callCount < 1 ? WebSocketState.Open : WebSocketState.Closed);
        ws.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            });

        // Should not throw even if shop provider fails
        await svc.HandleConnectionAsync("acct1", "dev1", ws, CancellationToken.None);
    }

    // ─── NotifyRivalsCompleteAsync ──────────────────────────

    [Fact]
    public async Task NotifyRivalsCompleteAsync_SendsCorrectType()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);
        svc.AddConnection("acct1", "dev1", ws);

        await svc.NotifyRivalsCompleteAsync("acct1");

        await ws.Received(1).SendAsync(
            Arg.Is<ArraySegment<byte>>(seg =>
                Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count).Contains("rivals_complete")),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }

    // ─── WebSocket subscribe/unsubscribe rebind ─────────────

    [Fact]
    public async Task HandleConnectionAsync_SubscribeSync_RebindsToRealAccountId()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        var subscribeJson = Encoding.UTF8.GetBytes("""{"action":"subscribe_sync","accountId":"real-acct"}""");

        int callCount = 0;
        ws.State.Returns(_ => callCount < 2 ? WebSocketState.Open : WebSocketState.Closed);
        ws.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 1)
                {
                    var buf = callInfo.ArgAt<ArraySegment<byte>>(0);
                    Array.Copy(subscribeJson, 0, buf.Array!, buf.Offset, subscribeJson.Length);
                    return new WebSocketReceiveResult(subscribeJson.Length, WebSocketMessageType.Text, true);
                }
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            });

        await svc.HandleConnectionAsync("anon-123", "dev1", ws, CancellationToken.None);

        // After subscribe, notify to "real-acct" should reach the socket
        ws.State.Returns(WebSocketState.Open);
        svc.AddConnection("real-acct", "dev1", ws); // Re-add since finally removed it
        await svc.NotifyAccountAsync("real-acct", new { type = "sync_progress" });

        await ws.Received().SendAsync(
            Arg.Is<ArraySegment<byte>>(seg =>
                Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count).Contains("sync_progress")),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleConnectionAsync_FragmentedSubscribeSync_SendsInitialSyncState()
    {
        var svc = CreateService();
        var tracker = new UserSyncProgressTracker(svc, Substitute.For<ILogger<UserSyncProgressTracker>>());
        svc.SetSyncTracker(tracker);
        tracker.BeginBackfill("real-acct", 10);

        var ws = Substitute.For<WebSocket>();
        var firstFragment = Encoding.UTF8.GetBytes("{\"action\":\"subscribe_sync\",\"acco");
        var secondFragment = Encoding.UTF8.GetBytes("untId\":\"real-acct\"}");

        int callCount = 0;
        ws.State.Returns(_ => callCount < 3 ? WebSocketState.Open : WebSocketState.Closed);
        ws.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                var buf = callInfo.ArgAt<ArraySegment<byte>>(0);
                if (callCount == 1)
                {
                    Array.Copy(firstFragment, 0, buf.Array!, buf.Offset, firstFragment.Length);
                    return new WebSocketReceiveResult(firstFragment.Length, WebSocketMessageType.Text, false);
                }

                if (callCount == 2)
                {
                    Array.Copy(secondFragment, 0, buf.Array!, buf.Offset, secondFragment.Length);
                    return new WebSocketReceiveResult(secondFragment.Length, WebSocketMessageType.Text, true);
                }

                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            });

        await svc.HandleConnectionAsync("anon-123", "dev1", ws, CancellationToken.None);

        await ws.Received().SendAsync(
            Arg.Is<ArraySegment<byte>>(seg => SegmentContains(seg, "\"type\":\"sync_progress\"", "\"accountId\":\"real-acct\"")),
            WebSocketMessageType.Text,
            true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleConnectionAsync_SubscribeSync_UsesDurableBackfillStateWhenTrackerIsQueued()
    {
        var svc = CreateService();
        var tracker = new UserSyncProgressTracker(svc, Substitute.For<ILogger<UserSyncProgressTracker>>());
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetBackfillStatus("real-acct").Returns(new BackfillStatusInfo
        {
            AccountId = "real-acct",
            Status = "in_progress",
            SongsChecked = 42,
            TotalSongsToCheck = 100,
            EntriesFound = 7,
        });
        svc.SetSyncTracker(tracker);
        svc.SetMetaDatabase(metaDb);
        tracker.BeginQueued("real-acct", 100);

        var ws = Substitute.For<WebSocket>();
        var subscribeJson = Encoding.UTF8.GetBytes("""{"action":"subscribe_sync","accountId":"real-acct"}""");

        int callCount = 0;
        ws.State.Returns(_ => callCount < 2 ? WebSocketState.Open : WebSocketState.Closed);
        ws.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                var buf = callInfo.ArgAt<ArraySegment<byte>>(0);
                if (callCount == 1)
                {
                    Array.Copy(subscribeJson, 0, buf.Array!, buf.Offset, subscribeJson.Length);
                    return new WebSocketReceiveResult(subscribeJson.Length, WebSocketMessageType.Text, true);
                }

                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            });

        await svc.HandleConnectionAsync("anon-123", "dev1", ws, CancellationToken.None);

        await ws.Received().SendAsync(
            Arg.Is<ArraySegment<byte>>(seg => SegmentContains(seg, "\"type\":\"sync_progress\"", "\"phase\":\"backfill\"", "\"itemsCompleted\":42")),
            WebSocketMessageType.Text,
            true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleConnectionAsync_SubscribeSync_PreservesDurableBackgroundRefreshForLivePostScrape()
    {
        var svc = CreateService();
        var tracker = new UserSyncProgressTracker(
            svc,
            Substitute.For<ILogger<UserSyncProgressTracker>>());
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetBackfillStatus("real-acct").Returns(new BackfillStatusInfo
        {
            AccountId = "real-acct",
            Status = "complete",
            DeferredReason = "catalog_refresh_queue",
        });
        svc.SetSyncTracker(tracker);
        svc.SetMetaDatabase(metaDb);
        tracker.BeginPostScrape("real-acct", 100);

        var ws = Substitute.For<WebSocket>();
        var subscribeJson = Encoding.UTF8.GetBytes(
            """{"action":"subscribe_sync","accountId":"real-acct"}""");
        var callCount = 0;
        ws.State.Returns(_ =>
            callCount < 2
                ? WebSocketState.Open
                : WebSocketState.Closed);
        ws.ReceiveAsync(
                Arg.Any<ArraySegment<byte>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                var buffer = callInfo.ArgAt<ArraySegment<byte>>(0);
                if (callCount == 1)
                {
                    Array.Copy(
                        subscribeJson,
                        0,
                        buffer.Array!,
                        buffer.Offset,
                        subscribeJson.Length);
                    return new WebSocketReceiveResult(
                        subscribeJson.Length,
                        WebSocketMessageType.Text,
                        true);
                }

                return new WebSocketReceiveResult(
                    0,
                    WebSocketMessageType.Close,
                    true);
            });

        await svc.HandleConnectionAsync(
            "anon-123",
            "dev1",
            ws,
            CancellationToken.None);

        await ws.Received().SendAsync(
            Arg.Is<ArraySegment<byte>>(segment =>
                SegmentContains(
                    segment,
                    "\"type\":\"sync_progress\"",
                    "\"phase\":\"postscrape\"",
                    "\"backgroundRefresh\":true")),
            WebSocketMessageType.Text,
            true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleConnectionAsync_SubscribeSync_DoesNotSurfacePendingHistory()
    {
        var svc = CreateService();
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetBackfillStatus("real-acct").Returns(new BackfillStatusInfo
        {
            AccountId = "real-acct",
            Status = "complete",
            EntriesFound = 72,
        });
        metaDb.GetHistoryReconStatus("real-acct").Returns(
            new HistoryReconStatusInfo
            {
                AccountId = "real-acct",
                Status = "pending",
            });
        svc.SetMetaDatabase(metaDb);

        var ws = Substitute.For<WebSocket>();
        var subscribeJson = Encoding.UTF8.GetBytes(
            """{"action":"subscribe_sync","accountId":"real-acct"}""");
        var callCount = 0;
        ws.State.Returns(_ =>
            callCount < 2
                ? WebSocketState.Open
                : WebSocketState.Closed);
        ws.ReceiveAsync(
                Arg.Any<ArraySegment<byte>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                var buffer = callInfo.ArgAt<ArraySegment<byte>>(0);
                if (callCount == 1)
                {
                    Array.Copy(
                        subscribeJson,
                        0,
                        buffer.Array!,
                        buffer.Offset,
                        subscribeJson.Length);
                    return new WebSocketReceiveResult(
                        subscribeJson.Length,
                        WebSocketMessageType.Text,
                        true);
                }

                return new WebSocketReceiveResult(
                    0,
                    WebSocketMessageType.Close,
                    true);
            });

        await svc.HandleConnectionAsync(
            "anon-123",
            "dev1",
            ws,
            CancellationToken.None);

        await ws.Received().SendAsync(
            Arg.Is<ArraySegment<byte>>(segment =>
                SegmentContains(
                    segment,
                    "\"type\":\"sync_progress\"",
                    "\"phase\":\"complete\"",
                    "\"entriesFound\":72")),
            WebSocketMessageType.Text,
            true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleConnectionAsync_SubscribeSync_OriginalKeyNoLongerReceives()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        var wsOther = Substitute.For<WebSocket>();
        wsOther.State.Returns(WebSocketState.Open);
        var subscribeJson = Encoding.UTF8.GetBytes("""{"action":"subscribe_sync","accountId":"real-acct"}""");

        int callCount = 0;
        ws.State.Returns(_ => callCount < 2 ? WebSocketState.Open : WebSocketState.Closed);
        ws.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 1)
                {
                    var buf = callInfo.ArgAt<ArraySegment<byte>>(0);
                    Array.Copy(subscribeJson, 0, buf.Array!, buf.Offset, subscribeJson.Length);
                    return new WebSocketReceiveResult(subscribeJson.Length, WebSocketMessageType.Text, true);
                }
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            });

        await svc.HandleConnectionAsync("anon-123", "dev1", ws, CancellationToken.None);

        // After subscribe + close, notifying "anon-123" should not reach any socket
        await svc.NotifyAccountAsync("anon-123", new { type = "test" });
        await wsOther.DidNotReceive().SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            Arg.Any<WebSocketMessageType>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleConnectionAsync_UnsubscribeSync_RevertsToOriginalKey()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        var subscribeJson = Encoding.UTF8.GetBytes("""{"action":"subscribe_sync","accountId":"real-acct"}""");
        var unsubscribeJson = Encoding.UTF8.GetBytes("""{"action":"unsubscribe_sync"}""");

        int callCount = 0;
        ws.State.Returns(_ => callCount < 3 ? WebSocketState.Open : WebSocketState.Closed);
        ws.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                var buf = callInfo.ArgAt<ArraySegment<byte>>(0);
                if (callCount == 1)
                {
                    Array.Copy(subscribeJson, 0, buf.Array!, buf.Offset, subscribeJson.Length);
                    return new WebSocketReceiveResult(subscribeJson.Length, WebSocketMessageType.Text, true);
                }
                if (callCount == 2)
                {
                    Array.Copy(unsubscribeJson, 0, buf.Array!, buf.Offset, unsubscribeJson.Length);
                    return new WebSocketReceiveResult(unsubscribeJson.Length, WebSocketMessageType.Text, true);
                }
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            });

        await svc.HandleConnectionAsync("anon-123", "dev1", ws, CancellationToken.None);

        // After unsubscribe + close, "real-acct" notifications should not reach the socket
        await svc.NotifyAccountAsync("real-acct", new { type = "test" });
        await ws.DidNotReceive().SendAsync(
            Arg.Is<ArraySegment<byte>>(seg =>
                Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count).Contains("\"test\"")),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleConnectionAsync_MalformedJson_DoesNotCrash()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        var badJson = Encoding.UTF8.GetBytes("{broken json!!!");

        int callCount = 0;
        ws.State.Returns(_ => callCount < 2 ? WebSocketState.Open : WebSocketState.Closed);
        ws.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 1)
                {
                    var buf = callInfo.ArgAt<ArraySegment<byte>>(0);
                    Array.Copy(badJson, 0, buf.Array!, buf.Offset, badJson.Length);
                    return new WebSocketReceiveResult(badJson.Length, WebSocketMessageType.Text, true);
                }
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            });

        // Should not throw
        await svc.HandleConnectionAsync("anon-123", "dev1", ws, CancellationToken.None);
    }

    [Fact]
    public async Task HandleConnectionAsync_SubscribeRebind_DisconnectCleansUpCorrectKey()
    {
        var svc = CreateService();
        var ws = Substitute.For<WebSocket>();
        var subscribeJson = Encoding.UTF8.GetBytes("""{"action":"subscribe_sync","accountId":"real-acct"}""");

        int callCount = 0;
        ws.State.Returns(_ => callCount < 2 ? WebSocketState.Open : WebSocketState.Closed);
        ws.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 1)
                {
                    var buf = callInfo.ArgAt<ArraySegment<byte>>(0);
                    Array.Copy(subscribeJson, 0, buf.Array!, buf.Offset, subscribeJson.Length);
                    return new WebSocketReceiveResult(subscribeJson.Length, WebSocketMessageType.Text, true);
                }
                // Simulate connection lost
                throw new WebSocketException("Connection reset");
            });

        await svc.HandleConnectionAsync("anon-123", "dev1", ws, CancellationToken.None);

        // After disconnect, "real-acct" should have been cleaned up by finally block
        // Notifying "real-acct" should not send anything
        await svc.NotifyAccountAsync("real-acct", new { type = "test" });
        await ws.DidNotReceive().SendAsync(
            Arg.Is<ArraySegment<byte>>(seg =>
                Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count).Contains("\"test\"")),
            WebSocketMessageType.Text, true,
            Arg.Any<CancellationToken>());
    }

    private static async Task
        AssertSourceOnlyWebSocketRebindSerializesWithPublicationCommitAsync(
            bool unsubscribe)
    {
        using var fixture = new Helpers.InMemoryMetaDatabase();
        var scrapeId = fixture.Db.StartScrapeRun();
        fixture.Db.CompleteScrapeRun(
            scrapeId,
            songsScraped: 1,
            totalEntries: 1,
            totalRequests: 1,
            totalBytes: 1);
        fixture.Db.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false);
        var pointers = fixture.Db.GetPublicationPointerState();
        var publicationId =
            Assert.IsType<long>(pointers.CurrentPublicationId);
        var publishedScrapeId =
            Assert.IsType<long>(pointers.PublishedScrapeId);
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.GetPublicationPointerState(
                Arg.Any<int>())
            .Returns(pointers);
        metaDb.GetPublicationSurfaceSourceEvidence(
                publicationId,
                PublicationSurfaceNames.SoloScopeSources,
                Arg.Any<int>())
            .Returns(new PublicationSurfaceSourceEvidence(
                PublicationSurfaceNames.SoloScopeSources,
                Exists: true,
                publicationId,
                publishedScrapeId,
                RowCount: 1,
                ContentHash: new string('a', 64)));
        var publicationService =
            new PublicationReadContextService(
                metaDb,
                fixture.DataSource,
                Options.Create(new FeatureOptions
                {
                    EnablePublicationReadContext = false,
                    UsePublishedScopeSources = true,
                }));
        var blockedAccountId =
            unsubscribe ? "real-acct" : "anon-123";
        var barrierLog =
            new RebindBarrierLogger(blockedAccountId);
        var notifications =
            new NotificationService(barrierLog);
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);
        var subscribeJson = Encoding.UTF8.GetBytes(
            """{"action":"subscribe_sync","accountId":"real-acct"}""");
        var unsubscribeJson = Encoding.UTF8.GetBytes(
            """{"action":"unsubscribe_sync"}""");
        var receiveCount = 0;
        ws.ReceiveAsync(
                Arg.Any<ArraySegment<byte>>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var count = Interlocked.Increment(
                    ref receiveCount);
                var buffer =
                    call.ArgAt<ArraySegment<byte>>(0);
                if (count == 1)
                {
                    Array.Copy(
                        subscribeJson,
                        0,
                        buffer.Array!,
                        buffer.Offset,
                        subscribeJson.Length);
                    return new WebSocketReceiveResult(
                        subscribeJson.Length,
                        WebSocketMessageType.Text,
                        true);
                }
                if (unsubscribe && count == 2)
                {
                    Array.Copy(
                        unsubscribeJson,
                        0,
                        buffer.Array!,
                        buffer.Offset,
                        unsubscribeJson.Length);
                    return new WebSocketReceiveResult(
                        unsubscribeJson.Length,
                        WebSocketMessageType.Text,
                        true);
                }

                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    call.ArgAt<CancellationToken>(1));
                return new WebSocketReceiveResult(
                    0,
                    WebSocketMessageType.Close,
                    true);
            });
        using var cancellation =
            new CancellationTokenSource();
        var connectionTask = Task.Run(
            () => notifications.HandleConnectionAsync(
                "anon-123",
                "dev1",
                ws,
                publicationId,
                publicationService,
                cancellation.Token));

        await barrierLog.RemovalEntered.WaitAsync(
            TimeSpan.FromSeconds(5));
        var commitLockAcquired =
            new TaskCompletionSource(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        var commitTask = Task.Run(async () =>
        {
            await using var connection =
                await fixture.DataSource
                    .OpenConnectionAsync();
            await using var transaction =
                await connection.BeginTransactionAsync();
            await using var command =
                connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "SELECT pg_advisory_xact_lock(@lockKey)";
            command.Parameters.AddWithValue(
                "lockKey",
                PublicationGenerationSchema
                    .AdvisoryLockKey);
            await command.ExecuteNonQueryAsync();
            commitLockAcquired.TrySetResult();
            await notifications.NotifyPublicationChangedAsync(
                publicationId + 1);
            await transaction.CommitAsync();
        });

        var earlyCommit =
            await Task.WhenAny(
                commitLockAcquired.Task,
                Task.Delay(
                    TimeSpan.FromMilliseconds(250)));
        var commitPassedRebind =
            ReferenceEquals(
                earlyCommit,
                commitLockAcquired.Task);

        barrierLog.ReleaseRemoval();
        await commitTask.WaitAsync(
            TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await connectionTask.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.False(
            commitPassedRebind,
            "Publication commit acquired its exclusive lock while a WebSocket rebind had removed but not re-registered the socket.");
        await ws.Received(1).SendAsync(
            Arg.Is<ArraySegment<byte>>(segment =>
                SegmentContains(
                    segment,
                    "\"type\":\"publication_changed\"",
                    $"\"publicationId\":{publicationId + 1}")),
            WebSocketMessageType.Text,
            true,
            Arg.Any<CancellationToken>());
        await ws.Received(1).CloseOutputAsync(
            WebSocketCloseStatus.PolicyViolation,
            "Publication changed",
            Arg.Any<CancellationToken>());
        metaDb.Received(unsubscribe ? 3 : 2)
            .GetPublicationSurfaceSourceEvidence(
                publicationId,
                PublicationSurfaceNames.SoloScopeSources,
                Arg.Any<int>());
    }

    private sealed class RebindBarrierLogger(
        string blockedAccountId)
        : ILogger<NotificationService>
    {
        private readonly TaskCompletionSource
            _removalEntered = new(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        private readonly TaskCompletionSource
            _releaseRemoval = new(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        private int _blocked;

        internal Task RemovalEntered =>
            _removalEntered.Task;

        public IDisposable? BeginScope<TState>(
            TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) =>
            true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (!message.Contains(
                    $"WebSocket disconnected: account={blockedAccountId},",
                    StringComparison.Ordinal)
                || Interlocked.Exchange(
                    ref _blocked,
                    1) != 0)
            {
                return;
            }

            _removalEntered.TrySetResult();
            if (!_releaseRemoval.Task.Wait(
                    TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException(
                    "Timed out waiting to release the WebSocket rebind barrier.");
            }
        }

        internal void ReleaseRemoval() =>
            _releaseRemoval.TrySetResult();
    }
}
