using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class BandPageFetcherTests
{
    private readonly ILogger _log = Substitute.For<ILogger>();

    [Fact]
    public async Task CompleteScopeProducesPageAndContentManifest()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueJsonOk(MakePage(0, 2, ("a", "b", 1000)));
        handler.EnqueueJsonOk(MakePage(1, 2, ("c", "d", 900)));

        await using var spool = CreateSpool();
        using var pool = new SharedDopPool(1, 1, 1, 100, _log);
        var fetcher = new BandPageFetcher(
            new ResilientHttpExecutor(new HttpClient(handler), _log),
            pool,
            spool,
            new ScrapeProgressTracker(),
            _log);

        await fetcher.FetchAllAsync(
            ["song_1"],
            ["Band_Duets"],
            "token",
            "acct",
            maxPages: 10,
            CancellationToken.None);

        var manifest = Assert.Single(fetcher.ScopeManifests).Manifest;
        Assert.True(manifest.IsComplete);
        Assert.Equal([0, 1], manifest.ReceivedPages);
        Assert.Equal(2, manifest.ReportedTotalPages);
        Assert.False(string.IsNullOrWhiteSpace(manifest.ContentFingerprint));
        Assert.Equal(2, spool.RecordCount);
        Assert.Equal(2, spool.EntryCount);
    }

    [Fact]
    public async Task MalformedPageIsVisibleAndIncomplete()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueJsonOk(MakePage(0, 2, ("a", "b", 1000)));
        handler.EnqueueJsonOk("{not-json");
        handler.EnqueueJsonOk("{still-not-json");

        await using var spool = CreateSpool();
        using var pool = new SharedDopPool(1, 1, 1, 100, _log);
        var fetcher = new BandPageFetcher(
            new ResilientHttpExecutor(new HttpClient(handler), _log),
            pool,
            spool,
            new ScrapeProgressTracker(),
            _log);

        await fetcher.FetchAllAsync(
            ["song_1"],
            ["Band_Duets"],
            "token",
            "acct",
            maxPages: 10,
            CancellationToken.None);

        var manifest = Assert.Single(fetcher.ScopeManifests).Manifest;
        Assert.False(manifest.IsComplete);
        Assert.Equal("failed", manifest.ParseStatus);
        Assert.Equal("parsefailure", manifest.PageStatuses[1]);
    }

    [Fact]
    public async Task EpicEmptyScopeIsComplete()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueJsonOk(MakePage(0, 0));

        await using var spool = CreateSpool();
        using var pool = new SharedDopPool(1, 1, 1, 100, _log);
        var fetcher = new BandPageFetcher(
            new ResilientHttpExecutor(new HttpClient(handler), _log),
            pool,
            spool,
            new ScrapeProgressTracker(),
            _log);

        await fetcher.FetchAllAsync(
            ["song_1"],
            ["Band_Duets"],
            "token",
            "acct",
            maxPages: 10,
            CancellationToken.None);

        var manifest = Assert.Single(fetcher.ScopeManifests).Manifest;
        Assert.True(manifest.IsComplete);
        Assert.Equal(ScopeTerminalBoundaryKind.EpicEmpty, manifest.TerminalBoundary);
        Assert.Equal([0], manifest.ReceivedPages);
    }

    [Fact]
    public async Task EventNotFoundScopeIsCompleteEmpty()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueJsonResponse(
            System.Net.HttpStatusCode.NotFound,
            """{"errorCode":"com.epicgames.events.event_not_found","errorMessage":"Event not found"}""");

        await using var spool = CreateSpool();
        using var pool = new SharedDopPool(1, 1, 1, 100, _log);
        var fetcher = new BandPageFetcher(
            new ResilientHttpExecutor(new HttpClient(handler), _log),
            pool,
            spool,
            new ScrapeProgressTracker(),
            _log);

        await fetcher.FetchAllAsync(
            ["new-song"],
            ["Band_Duets"],
            "token",
            "acct",
            maxPages: 10,
            CancellationToken.None);

        var manifest = Assert.Single(fetcher.ScopeManifests).Manifest;
        Assert.True(manifest.IsComplete);
        Assert.Equal(ScopeTerminalBoundaryKind.EpicEmpty, manifest.TerminalBoundary);
        Assert.Equal("success", manifest.PageStatuses[0]);
        Assert.Equal([0], manifest.ReceivedPages);
    }

    private SpoolWriter<BandLeaderboardEntry> CreateSpool() =>
        new(
            _log,
            "band-test",
            serialize: (buffer, header, songId, entries) =>
            {
                SpoolWriter<BandLeaderboardEntry>.WriteString(buffer, header, songId);
                SpoolWriter<BandLeaderboardEntry>.WriteInt32(buffer, header, entries.Count);
            },
            deserialize: (stream, header) =>
            {
                var songId = SpoolWriter<BandLeaderboardEntry>.ReadString(stream, header);
                _ = SpoolWriter<BandLeaderboardEntry>.ReadInt32(stream, header);
                return (songId, Array.Empty<BandLeaderboardEntry>());
            },
            flush: (_, _) => { });

    private static string MakePage(
        int page,
        int totalPages,
        params (string First, string Second, int Score)[] entries)
    {
        var rows = string.Join(
            ",",
            entries.Select((entry, index) =>
                $$"""{"teamAccountIds":["{{entry.First}}","{{entry.Second}}"],"rank":{{page * 100 + index + 1}},"percentile":0.5,"score":{{entry.Score}}}"""));
        return $$"""{"page":{{page}},"totalPages":{{totalPages}},"entries":[{{rows}}]}""";
    }
}
