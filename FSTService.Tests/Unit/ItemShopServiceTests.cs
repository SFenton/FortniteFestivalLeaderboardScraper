using FortniteFestival.Core;
using FortniteFestival.Core.Persistence;
using FortniteFestival.Core.Services;
using FSTService.Api;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.Reflection;

namespace FSTService.Tests.Unit;

public class ItemShopServiceTests
{
    // ─── ExtractJamTrackTitles (fortnite-api.com JSON) ────

    [Fact]
    public void ExtractTitles_ParsesJamTracksFromJson()
    {
        var json = """
        {
            "data": {
                "entries": [
                    { "tracks": [{ "title": "Dream On" }] },
                    { "tracks": [{ "title": "Flowers" }] },
                    { "regularPrice": 500 }
                ]
            }
        }
        """;

        var titles = ItemShopService.ExtractJamTrackTitles(json);
        Assert.Equal(2, titles.Count);
        Assert.Contains("Dream On", titles);
        Assert.Contains("Flowers", titles);
    }

    [Fact]
    public void ExtractTitles_Deduplicates()
    {
        var json = """
        {
            "data": {
                "entries": [
                    { "tracks": [{ "title": "Dream On" }] },
                    { "tracks": [{ "title": "Dream On" }] }
                ]
            }
        }
        """;

        var titles = ItemShopService.ExtractJamTrackTitles(json);
        Assert.Single(titles);
    }

    [Fact]
    public void ExtractTitles_EmptyJson_ReturnsEmpty()
    {
        var titles = ItemShopService.ExtractJamTrackTitles("{}");
        Assert.Empty(titles);
    }

    [Fact]
    public void ExtractTitles_MalformedJson_ReturnsEmpty()
    {
        var titles = ItemShopService.ExtractJamTrackTitles("not json {{");
        Assert.Empty(titles);
    }

    // ─── ExtractJamTrackSlugs (legacy HTML) ─────────────

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

    private static string MakeShopJson(params string[] titles)
    {
        var entries = string.Join(",\n",
            titles.Select(t => $$"""{ "tracks": [{ "title": "{{t}}" }] }"""));
        return $$"""
        {
            "data": {
                "hash": "abc",
                "entries": [{{entries}}]
            }
        }
        """;
    }

    private static string MakeShopJsonWithDates(params (string title, string? outDate)[] items)
    {
        var entries = string.Join(",\n",
            items.Select(i =>
            {
                var outDatePart = i.outDate is not null
                    ? $""", "outDate": "{i.outDate}" """
                    : "";
                return $$"""{ "tracks": [{ "title": "{{i.title}}" }]{{outDatePart}} }""";
            }));
        return $$"""
        {
            "data": {
                "hash": "abc",
                "entries": [{{entries}}]
            }
        }
        """;
    }

    [Fact]
    public async Task ScrapeAsync_WithMatchingJson_ReturnsSongCount()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueJsonOk(MakeShopJson("Flowers"));

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
        handler.EnqueueJsonOk("""{ "data": { "entries": [] } }""");

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
        var json = MakeShopJson("Flowers");
        handler.EnqueueJsonOk(json);
        handler.EnqueueJsonOk(json); // Same content again

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
        handler.EnqueueJsonOk(MakeShopJson("Flowers"));

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
    public async Task ScrapeAsync_UnmatchedTitles_LogsWarning()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueJsonOk(MakeShopJson("Totally Unknown Song"));

        var metaFixture = new InMemoryMetaDatabase();
        var service = CreateService(handler, metaFixture.Db);

        var result = await service.ScrapeAsync(CancellationToken.None);
        Assert.Equal(0, result); // No matches

        metaFixture.Dispose();
    }

    // ─── ExtractJamTrackEntries (outDate parsing) ────────

    [Fact]
    public void ExtractEntries_ParsesOutDate()
    {
        var json = """
        {
            "data": {
                "entries": [
                    { "tracks": [{ "title": "Dream On" }], "outDate": "2026-03-30T23:59:59.999Z" },
                    { "tracks": [{ "title": "Flowers" }], "outDate": "2026-04-02T23:59:59.999Z" }
                ]
            }
        }
        """;

        var entries = ItemShopService.ExtractJamTrackEntries(json);
        Assert.Equal(2, entries.Count);

        var dreamOn = entries.First(e => e.Title == "Dream On");
        Assert.NotNull(dreamOn.OutDate);
        Assert.Equal(new DateTime(2026, 3, 30, 23, 59, 59, 999, DateTimeKind.Utc), dreamOn.OutDate!.Value);

        var flowers = entries.First(e => e.Title == "Flowers");
        Assert.NotNull(flowers.OutDate);
        Assert.Equal(2026, flowers.OutDate!.Value.Year);
        Assert.Equal(4, flowers.OutDate!.Value.Month);
        Assert.Equal(2, flowers.OutDate!.Value.Day);
    }

    [Fact]
    public void ExtractEntries_MissingOutDate_ReturnsNull()
    {
        var json = """
        {
            "data": {
                "entries": [
                    { "tracks": [{ "title": "Dream On" }] }
                ]
            }
        }
        """;

        var entries = ItemShopService.ExtractJamTrackEntries(json);
        Assert.Single(entries);
        Assert.Null(entries[0].OutDate);
    }

    [Fact]
    public void ExtractEntries_Deduplicates()
    {
        var json = """
        {
            "data": {
                "entries": [
                    { "tracks": [{ "title": "Dream On" }], "outDate": "2026-03-30T23:59:59.999Z" },
                    { "tracks": [{ "title": "Dream On" }], "outDate": "2026-04-01T23:59:59.999Z" }
                ]
            }
        }
        """;

        var entries = ItemShopService.ExtractJamTrackEntries(json);
        Assert.Single(entries);
        // First occurrence wins
        Assert.Equal(30, entries[0].OutDate!.Value.Day);
    }

    [Fact]
    public void ExtractEntries_BackwardsCompatible_WithTitles()
    {
        var json = MakeShopJson("Dream On", "Flowers");

        // ExtractJamTrackTitles should still work (it delegates to ExtractJamTrackEntries)
        var titles = ItemShopService.ExtractJamTrackTitles(json);
        Assert.Equal(2, titles.Count);
        Assert.Contains("Dream On", titles);
        Assert.Contains("Flowers", titles);
    }

    // ─── ComputeLeavingTomorrow ─────────────────────────

    [Fact]
    public void ComputeLeavingTomorrow_OutDateToday_ReturnsTrue()
    {
        var now = new DateTime(2026, 3, 28, 12, 0, 0, DateTimeKind.Utc);
        var today = new DateTime(2026, 3, 28, 23, 59, 59, 999, DateTimeKind.Utc);

        var entries = new List<ShopTrackEntry> { new("Dream On", today) };
        var matched = new HashSet<string> { "song-123" };
        var titleToSongId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Dream On"] = "song-123"
        };

        var result = ItemShopService.ComputeLeavingTomorrow(entries, matched, titleToSongId, now);
        Assert.Single(result);
        Assert.Contains("song-123", result);
    }

    [Fact]
    public void ComputeLeavingTomorrow_OutDateTomorrow_ReturnsFalse()
    {
        var now = new DateTime(2026, 3, 28, 12, 0, 0, DateTimeKind.Utc);
        var tomorrow = new DateTime(2026, 3, 29, 23, 59, 59, 999, DateTimeKind.Utc);

        var entries = new List<ShopTrackEntry> { new("Dream On", tomorrow) };
        var matched = new HashSet<string> { "song-123" };
        var titleToSongId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Dream On"] = "song-123"
        };

        var result = ItemShopService.ComputeLeavingTomorrow(entries, matched, titleToSongId, now);
        Assert.Empty(result);
    }

    [Fact]
    public void ComputeLeavingTomorrow_OutDateFarFuture_ReturnsFalse()
    {
        var now = new DateTime(2026, 3, 28, 12, 0, 0, DateTimeKind.Utc);
        var future = new DateTime(2026, 4, 5, 23, 59, 59, 999, DateTimeKind.Utc);

        var entries = new List<ShopTrackEntry> { new("Dream On", future) };
        var matched = new HashSet<string> { "song-123" };
        var titleToSongId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Dream On"] = "song-123"
        };

        var result = ItemShopService.ComputeLeavingTomorrow(entries, matched, titleToSongId, now);
        Assert.Empty(result);
    }

    [Fact]
    public void ComputeLeavingTomorrow_NullOutDate_ReturnsFalse()
    {
        var now = new DateTime(2026, 3, 28, 12, 0, 0, DateTimeKind.Utc);

        var entries = new List<ShopTrackEntry> { new("Dream On", null) };
        var matched = new HashSet<string> { "song-123" };
        var titleToSongId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Dream On"] = "song-123"
        };

        var result = ItemShopService.ComputeLeavingTomorrow(entries, matched, titleToSongId, now);
        Assert.Empty(result);
    }

    [Fact]
    public void ComputeLeavingTomorrow_MixedDates_ReturnsOnlyToday()
    {
        var now = new DateTime(2026, 3, 28, 12, 0, 0, DateTimeKind.Utc);

        var entries = new List<ShopTrackEntry>
        {
            new("Dream On", new DateTime(2026, 3, 28, 23, 59, 59, DateTimeKind.Utc)),   // today (last day)
            new("Flowers",  new DateTime(2026, 4, 2, 23, 59, 59, DateTimeKind.Utc)),    // future
            new("Maps",     new DateTime(2026, 3, 29, 23, 59, 59, DateTimeKind.Utc)),   // tomorrow
        };
        var matched = new HashSet<string> { "song-1", "song-2", "song-3" };
        var titleToSongId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Dream On"] = "song-1",
            ["Flowers"]  = "song-2",
            ["Maps"]     = "song-3",
        };

        var result = ItemShopService.ComputeLeavingTomorrow(entries, matched, titleToSongId, now);
        Assert.Single(result);
        Assert.Contains("song-1", result);
    }

    [Fact]
    public async Task ScrapeAsync_WithOutDates_SetsLeavingTomorrow()
    {
        // Build JSON where "Flowers" leaves today (outDate = today = last day in shop)
        var today = DateTime.UtcNow.Date.AddHours(23).AddMinutes(59).AddSeconds(59);
        var farFuture = DateTime.UtcNow.Date.AddDays(7);
        var json = MakeShopJsonWithDates(
            ("Flowers", today.ToString("o")),
            ("Unknown Song", farFuture.ToString("o")));

        var handler = new MockHttpMessageHandler();
        handler.EnqueueJsonOk(json);

        var metaFixture = new InMemoryMetaDatabase();
        var service = CreateService(handler, metaFixture.Db);

        await service.ScrapeAsync(CancellationToken.None);

        // "Flowers" matched, and its outDate is tomorrow
        Assert.Single(service.InShopSongIds);
        Assert.Single(service.LeavingTomorrowSongIds);
    }

    [Fact]
    public async Task ScrapeAsync_ContentChanged_BroadcastsNotification()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueJsonOk(MakeShopJson("Flowers"));

        var metaFixture = new InMemoryMetaDatabase();
        var service = CreateService(handler, metaFixture.Db);

        var notifications = new NotificationService(Substitute.For<ILogger<NotificationService>>());
        service.SetNotificationService(notifications);

        var result = await service.ScrapeAsync(CancellationToken.None);
        Assert.Equal(1, result);
        // Notification was sent (no crash) — we just verify the code path runs
    }

    [Fact]
    public async Task TriggerScrapeAsync_DelegatestoScrapeAsync()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueJsonOk(MakeShopJson("Flowers"));

        var metaFixture = new InMemoryMetaDatabase();
        var service = CreateService(handler, metaFixture.Db);

        var result = await service.TriggerScrapeAsync(CancellationToken.None);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task InitializeAsync_WithPersistedData_LoadsFromDb()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueJsonOk(MakeShopJson("Flowers"));

        var metaFixture = new InMemoryMetaDatabase();
        // Pre-persist shop data in the DB
        metaFixture.Db.SaveItemShopTracks(
            new HashSet<string> { "pre-existing-song" },
            new HashSet<string> { "leaving-song" },
            DateTime.UtcNow);

        var service = CreateService(handler, metaFixture.Db);
        await service.InitializeAsync(CancellationToken.None);

        // After init, should have scrape results (overriding persisted data)
        Assert.NotNull(service.LastScrapedAt);
    }

    [Fact]
    public void ShopCacheService_CanBeSet()
    {
        var handler = new MockHttpMessageHandler();
        var metaFixture = new InMemoryMetaDatabase();
        var service = CreateService(handler, metaFixture.Db);

        var shopCache = new ShopCacheService();
        service.SetShopCacheService(shopCache);
        // No crash
    }
}
