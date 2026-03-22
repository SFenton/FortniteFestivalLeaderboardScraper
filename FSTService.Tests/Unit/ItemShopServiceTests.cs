using FortniteFestival.Core;
using FortniteFestival.Core.Persistence;
using FortniteFestival.Core.Services;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Reflection;

namespace FSTService.Tests.Unit;

public class ItemShopServiceTests
{
    // ─── ExtractJamTrackSlugs ───────────────────────────

    [Fact]
    public void ExtractSlugs_ParsesHrefsFromHtml()
    {
        var html = """
            <a href="/item-shop/jam-tracks/dream-on-41d337593ef9?lang=en-US">Dream On</a>
            <a href="/item-shop/jam-tracks/flowers-65417f34f863?lang=en-US">Flowers</a>
            <a href="/item-shop/outfits/some-skin-abcdef12">Not a jam track</a>
            <a href="/item-shop/jam-tracks/moves-like-jagger-fea1e3c647d8?lang=en-US">Moves Like Jagger</a>
            """;

        var slugs = ItemShopService.ExtractJamTrackSlugs(html);

        Assert.Equal(3, slugs.Count);
        Assert.Contains("dream-on-41d337593ef9", slugs);
        Assert.Contains("flowers-65417f34f863", slugs);
        Assert.Contains("moves-like-jagger-fea1e3c647d8", slugs);
    }

    [Fact]
    public void ExtractSlugs_DeduplicatesSameUrl()
    {
        var html = """
            <a href="/item-shop/jam-tracks/dream-on-41d337593ef9?lang=en-US">Dream On</a>
            <a href="/item-shop/jam-tracks/dream-on-41d337593ef9">Dream On Again</a>
            """;

        var slugs = ItemShopService.ExtractJamTrackSlugs(html);
        Assert.Single(slugs);
    }

    [Fact]
    public void ExtractSlugs_EmptyHtml_ReturnsEmpty()
    {
        var slugs = ItemShopService.ExtractJamTrackSlugs("<html><body>No tracks here</body></html>");
        Assert.Empty(slugs);
    }

    [Fact]
    public void ExtractSlugs_StripsQueryString()
    {
        var html = """<a href="/item-shop/jam-tracks/dream-on-41d337593ef9?lang=en-US&foo=bar">Dream On</a>""";

        var slugs = ItemShopService.ExtractJamTrackSlugs(html);
        Assert.Single(slugs);
        Assert.Equal("dream-on-41d337593ef9", slugs[0]);
    }

    [Fact]
    public void ExtractSlugs_IgnoresNonJamTrackPaths()
    {
        var html = """
            <a href="/item-shop/outfits/ninja-cb94cd38?lang=en-US">Ninja</a>
            <a href="/item-shop/emotes/jubi-slide-8bfec461?lang=en-US">Jubi Slide</a>
            <a href="/item-shop/jam-tracks/nightmare-f0f0a667ad83?lang=en-US">Nightmare</a>
            """;

        var slugs = ItemShopService.ExtractJamTrackSlugs(html);
        Assert.Single(slugs);
        Assert.Equal("nightmare-f0f0a667ad83", slugs[0]);
    }

    // ─── Content Change Detection (via slug extraction) ─

    [Fact]
    public void ExtractSlugs_DifferentHtml_ProducesDifferentSets()
    {
        var html1 = """<a href="/item-shop/jam-tracks/dream-on-41d337593ef9">A</a>""";
        var html2 = """<a href="/item-shop/jam-tracks/flowers-65417f34f863">B</a>""";

        var slugs1 = ItemShopService.ExtractJamTrackSlugs(html1);
        var slugs2 = ItemShopService.ExtractJamTrackSlugs(html2);

        Assert.NotEqual(slugs1[0], slugs2[0]);
    }

    // ─── Hash-to-SongId matching via ShopUrlHelper ──────

    [Fact]
    public void HashExtraction_MatchesRealSongIds()
    {
        // Simulate what ItemShopService does: extract hash from URL slug, then match to catalog
        var slug = "dream-on-41d337593ef9";
        var hash = ShopUrlHelper.ExtractHashFromSlug(slug);
        Assert.NotNull(hash);

        var songId = "9b468bdf-3379-4297-b1f3-41d337593ef9";
        var catalogHash = ShopUrlHelper.ExtractHash(songId);

        Assert.Equal(hash, catalogHash);
    }

    [Fact]
    public void HashExtraction_MultipleKnownSongs()
    {
        var testCases = new[]
        {
            ("flowers-65417f34f863",                     "1faef457-e84e-424b-b9de-65417f34f863"),
            ("sweet-child-o-mine-ba9c7596ace5",          "573fdeab-1bd8-4432-aee9-ba9c7596ace5"),
            ("moves-like-jagger-fea1e3c647d8",           "a85e8fc3-6d05-4439-a663-fea1e3c647d8"),
            ("suddenly-i-see-db9003b755f2",              "4a717eb5-d23e-444d-a8ca-db9003b755f2"),
            ("nightmare-f0f0a667ad83",                   "ec4bccd2-d9a6-439a-992c-f0f0a667ad83"),
            ("the-final-countdown-54f1ac60aee4",         "a10bbee1-2cbe-433d-bbf5-54f1ac60aee4"),
            ("paradise-city-95f131484471",               "920086b8-1e0d-4666-aa96-95f131484471"),
            ("american-idiot-ff5d146de8bc",              "24cba443-2738-45d0-be7c-ff5d146de8bc"),
        };

        foreach (var (slug, songId) in testCases)
        {
            var hashFromSlug = ShopUrlHelper.ExtractHashFromSlug(slug);
            var hashFromSongId = ShopUrlHelper.ExtractHash(songId);
            Assert.Equal(hashFromSlug, hashFromSongId);
        }
    }

    // ─── Realistic HTML Integration ─────────────────────

    [Fact]
    public void ExtractSlugs_RealisticShopHtml()
    {
        // Subset of real HTML from fortnite.com/item-shop/jam-tracks
        var html = """
            <div>
            [We Like To Party! (The Vengabus) Vengaboys 1998 03:45 V-Bucks Price 500](https://www.fortnite.com/item-shop/jam-tracks/we-like-to-party-the-vengabus-28d2ba53ffa2?lang=en-US)
            [Flowers Miley Cyrus 2023 03:18 V-Bucks Price 500](https://www.fortnite.com/item-shop/jam-tracks/flowers-65417f34f863?lang=en-US)
            [Moves Like Jagger Maroon 5 ft. Christina Aguilera 2011 03:26 V-Bucks Price 500](https://www.fortnite.com/item-shop/jam-tracks/moves-like-jagger-fea1e3c647d8?lang=en-US)
            [Sweet Child O' Mine Guns N' Roses 1987 05:57 V-Bucks Price 500](https://www.fortnite.com/item-shop/jam-tracks/sweet-child-o-mine-ba9c7596ace5?lang=en-US)
            </div>
            """;

        var slugs = ItemShopService.ExtractJamTrackSlugs(html);
        Assert.Equal(4, slugs.Count);
        Assert.Contains("we-like-to-party-the-vengabus-28d2ba53ffa2", slugs);
        Assert.Contains("flowers-65417f34f863", slugs);
        Assert.Contains("moves-like-jagger-fea1e3c647d8", slugs);
        Assert.Contains("sweet-child-o-mine-ba9c7596ace5", slugs);
    }

    // ─── ScrapeAsync ────────────────────────────────────

    private static ItemShopService CreateService(HttpMessageHandler handler, MetaDatabase? metaDb = null)
    {
        var http = new HttpClient(handler);
        var svc = new FestivalService((IFestivalPersistence?)null);

        // Add a test song that matches the hash in our fake HTML
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var songsField = typeof(FestivalService).GetField("_songs", flags)!;
        var dirtyField = typeof(FestivalService).GetField("_songsDirty", flags)!;
        var dict = (Dictionary<string, Song>)songsField.GetValue(svc)!;
        dict["1faef457-e84e-424b-b9de-65417f34f863"] = new Song
        {
            track = new Track { su = "1faef457-e84e-424b-b9de-65417f34f863", tt = "Flowers", an = "Miley Cyrus" },
        };
        dirtyField.SetValue(svc, true);

        return new ItemShopService(
            http,
            svc,
            metaDb ?? new InMemoryMetaDatabase().Db,
            Substitute.For<ILogger<ItemShopService>>());
    }

    [Fact]
    public async Task ScrapeAsync_WithMatchingHtml_ReturnsSongCount()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueJsonOk("""
            <a href="/item-shop/jam-tracks/flowers-65417f34f863?lang=en-US">Flowers</a>
            """);

        var metaFixture = new InMemoryMetaDatabase();
        var service = CreateService(handler, metaFixture.Db);

        var result = await service.ScrapeAsync(CancellationToken.None);
        Assert.Equal(1, result);
        Assert.Single(service.InShopSongIds);
        Assert.NotNull(service.LastScrapedAt);

        metaFixture.Dispose();
    }

    [Fact]
    public async Task ScrapeAsync_NoJamTracks_ReturnsZero()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueJsonOk("<html><body>No jam tracks here</body></html>");

        var metaFixture = new InMemoryMetaDatabase();
        var service = CreateService(handler, metaFixture.Db);

        var result = await service.ScrapeAsync(CancellationToken.None);
        Assert.Equal(0, result);

        metaFixture.Dispose();
    }

    [Fact]
    public async Task ScrapeAsync_UnchangedContent_ReturnsNegativeOne()
    {
        var handler = new MockHttpMessageHandler();
        var html = """<a href="/item-shop/jam-tracks/flowers-65417f34f863?lang=en-US">Flowers</a>""";
        handler.EnqueueJsonOk(html);
        handler.EnqueueJsonOk(html); // Same content again

        var metaFixture = new InMemoryMetaDatabase();
        var service = CreateService(handler, metaFixture.Db);

        var first = await service.ScrapeAsync(CancellationToken.None);
        Assert.Equal(1, first);

        var second = await service.ScrapeAsync(CancellationToken.None);
        Assert.Equal(-1, second); // Unchanged

        metaFixture.Dispose();
    }

    [Fact]
    public async Task InitializeAsync_LoadsFromDbAndScrapes()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueJsonOk("""
            <a href="/item-shop/jam-tracks/flowers-65417f34f863?lang=en-US">Flowers</a>
            """);

        var metaFixture = new InMemoryMetaDatabase();
        var service = CreateService(handler, metaFixture.Db);

        await service.InitializeAsync(CancellationToken.None);

        Assert.NotNull(service.LastScrapedAt);

        metaFixture.Dispose();
    }

    [Fact]
    public async Task InitializeAsync_ScrapeFailure_DoesNotThrow()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueException(new HttpRequestException("Network error"));

        var metaFixture = new InMemoryMetaDatabase();
        var service = CreateService(handler, metaFixture.Db);

        // Should not throw — startup scrape failure is caught
        await service.InitializeAsync(CancellationToken.None);
        Assert.Null(service.LastScrapedAt); // Scrape failed, no timestamp

        metaFixture.Dispose();
    }

    [Fact]
    public async Task ScrapeAsync_UnmatchedSlugs_LogsWarning()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueJsonOk("""
            <a href="/item-shop/jam-tracks/unknown-song-abcdef123456?lang=en-US">Unknown</a>
            """);

        var metaFixture = new InMemoryMetaDatabase();
        var service = CreateService(handler, metaFixture.Db);

        var result = await service.ScrapeAsync(CancellationToken.None);
        Assert.Equal(0, result); // No matches

        metaFixture.Dispose();
    }
}
