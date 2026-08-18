using System.Net;
using System.Text;
using FortniteFestival.Core;
using FortniteFestival.Core.Persistence;
using FortniteFestival.Core.Services;
using FSTService.Tests.Helpers;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FSTService.Tests.Unit;

public sealed class SongCatalogRefreshWorkerTests
{
    [Fact]
    public async Task RefreshCatalogAsync_logs_busy_and_allows_next_interval_retry()
    {
        var persistence = new BusyCatalogPersistence();
        var service = new FestivalService(
            persistence,
            CreateProviderClient());
        var logger = new TestLogger<SongCatalogRefreshWorker>();
        var worker = new SongCatalogRefreshWorker(
            service,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            Options.Create(new ScraperOptions
            {
                EnablePathGeneration = false,
                EnableAutomaticPathGeneration = false,
            }),
            Options.Create(new JsonOptions()),
            logger);

        await worker.RefreshCatalogAsync(CancellationToken.None);
        await worker.RefreshCatalogAsync(CancellationToken.None);

        Assert.Equal(2, persistence.SaveAttempts);
        Assert.Empty(service.Songs);
        var deferred = logger.Entries
            .Where(static entry =>
                entry.Level == LogLevel.Warning
                && entry.Message.Contains(
                    "publication persistence is busy",
                    StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, deferred.Length);
        Assert.All(
            deferred,
            static entry =>
                Assert.IsType<SongCatalogPersistenceBusyException>(
                    entry.Exception));
        Assert.DoesNotContain(
            logger.Entries,
            static entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task InitializeAsync_surfaces_retryable_catalog_busy()
    {
        var persistence = new BusyCatalogPersistence();
        var service = new FestivalService(
            persistence,
            CreateProviderClient());

        await Assert.ThrowsAsync<SongCatalogPersistenceBusyException>(
            service.InitializeAsync);

        Assert.Equal(1, persistence.SaveAttempts);
        Assert.Empty(service.Songs);
    }

    [Fact]
    public void Exact_catalog_revision_detects_metadata_changes_without_count_change()
    {
        var changed = new SongCatalogSyncResult(
            providerRequestSucceeded: true,
            isExact: true,
            safetyMergeApplied: false,
            providerSongCount: 700,
            catalogSongCount: 700,
            droppedProviderObjectCount: 0,
            failureReason: null!,
            persistenceToken:
                new SongCatalogPersistenceToken(
                    12,
                    2,
                    "new-hash",
                    700));
        var unchanged = new SongCatalogSyncResult(
            providerRequestSucceeded: true,
            isExact: true,
            safetyMergeApplied: false,
            providerSongCount: 700,
            catalogSongCount: 700,
            droppedProviderObjectCount: 0,
            failureReason: null!,
            persistenceToken:
                new SongCatalogPersistenceToken(
                    11,
                    2,
                    "old-hash",
                    700));
        var inexact = new SongCatalogSyncResult(
            providerRequestSucceeded: true,
            isExact: false,
            safetyMergeApplied: true,
            providerSongCount: 650,
            catalogSongCount: 700,
            droppedProviderObjectCount: 1,
            failureReason: "partial",
            persistenceToken: null!);

        Assert.True(
            SongCatalogRefreshWorker
                .HasExactCatalogChanged(
                    "old-hash",
                    changed));
        Assert.False(
            SongCatalogRefreshWorker
                .HasExactCatalogChanged(
                    "old-hash",
                    unchanged));
        Assert.False(
            SongCatalogRefreshWorker
                .HasExactCatalogChanged(
                    "old-hash",
                    inexact));
    }

    private static HttpClient CreateProviderClient() =>
        new(new ProviderHandler())
        {
            BaseAddress = new Uri(
                "https://fortnitecontent-website-prod07.ol.epicgames.com"),
        };

    private sealed class ProviderHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "song-a": {
                        "_title": "Alpha",
                        "track": {
                          "su": "song-a",
                          "tt": "Alpha",
                          "an": "Artist"
                        }
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            });
    }

    private sealed class BusyCatalogPersistence :
        IFestivalPersistence,
        IVersionedSongCatalogPersistence
    {
        public int SaveAttempts { get; private set; }

        public Task<IList<LeaderboardData>> LoadScoresAsync() =>
            Task.FromResult<IList<LeaderboardData>>([]);

        public Task SaveScoresAsync(IEnumerable<LeaderboardData> scores) =>
            Task.CompletedTask;

        public Task<IList<Song>> LoadSongsAsync() =>
            Task.FromResult<IList<Song>>([]);

        public Task SaveSongsAsync(IEnumerable<Song> songs) =>
            Task.CompletedTask;

        public Task<SongCatalogPersistenceToken> SaveSongsVersionedAsync(
            IEnumerable<Song> songs)
        {
            SaveAttempts++;
            return Task.FromException<SongCatalogPersistenceToken>(
                new SongCatalogPersistenceBusyException(
                    "Injected retryable publication contention."));
        }
    }
}
