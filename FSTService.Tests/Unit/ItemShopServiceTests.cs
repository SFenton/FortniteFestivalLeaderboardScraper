using FSTService.Scraping;

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
}
