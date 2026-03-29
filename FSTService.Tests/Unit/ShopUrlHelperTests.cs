using FSTService.Scraping;

namespace FSTService.Tests.Unit;

public class ShopUrlHelperTests
{
    // ─── ComputeShopUrl ─────────────────────────────────

    [Fact]
    public void DreamOn_ComputesCorrectUrl()
    {
        var url = ShopUrlHelper.ComputeShopUrl(
            "9b468bdf-3379-4297-b1f3-41d337593ef9", "Dream On");

        Assert.Equal(
            "https://www.fortnite.com/item-shop/jam-tracks/dream-on-41d337593ef9",
            url);
    }

    [Fact]
    public void SweetChildOMine_ApostropheHandling()
    {
        var url = ShopUrlHelper.ComputeShopUrl(
            "573fdeab-1bd8-4432-aee9-ba9c7596ace5", "Sweet Child O' Mine");

        Assert.Equal(
            "https://www.fortnite.com/item-shop/jam-tracks/sweet-child-o-mine-ba9c7596ace5",
            url);
    }

    [Fact]
    public void MovesLikeJagger_CorrectHash()
    {
        var url = ShopUrlHelper.ComputeShopUrl(
            "a85e8fc3-6d05-4439-a663-fea1e3c647d8", "Moves Like Jagger");

        Assert.Equal(
            "https://www.fortnite.com/item-shop/jam-tracks/moves-like-jagger-fea1e3c647d8",
            url);
    }

    [Fact]
    public void Flowers_SimpleTitle()
    {
        var url = ShopUrlHelper.ComputeShopUrl(
            "1faef457-e84e-424b-b9de-65417f34f863", "Flowers");

        Assert.Equal(
            "https://www.fortnite.com/item-shop/jam-tracks/flowers-65417f34f863",
            url);
    }

    [Fact]
    public void SuddenlyISee_VerifiedUrl()
    {
        var url = ShopUrlHelper.ComputeShopUrl(
            "4a717eb5-d23e-444d-a8ca-db9003b755f2", "Suddenly I See");

        Assert.Equal(
            "https://www.fortnite.com/item-shop/jam-tracks/suddenly-i-see-db9003b755f2",
            url);
    }

    [Fact]
    public void BlingBangBangBorn_HyphensInTitleDropped()
    {
        var url = ShopUrlHelper.ComputeShopUrl(
            "3cae5d92-14b7-450f-b37c-54f52fc75340", "Bling-Bang-Bang-Born");

        Assert.Equal(
            "https://www.fortnite.com/item-shop/jam-tracks/blingbangbangborn-54f52fc75340",
            url);
    }

    // ─── ExtractHash ────────────────────────────────────

    [Fact]
    public void ExtractHash_ExtractsLast12Chars()
    {
        var hash = ShopUrlHelper.ExtractHash("9b468bdf-3379-4297-b1f3-41d337593ef9");
        Assert.Equal("41d337593ef9", hash);
    }

    [Fact]
    public void ExtractHash_DifferentUuid()
    {
        var hash = ShopUrlHelper.ExtractHash("a85e8fc3-6d05-4439-a663-fea1e3c647d8");
        Assert.Equal("fea1e3c647d8", hash);
    }

    // ─── ExtractHashFromSlug ────────────────────────────

    [Fact]
    public void ExtractHashFromSlug_ValidSlug()
    {
        var hash = ShopUrlHelper.ExtractHashFromSlug("dream-on-41d337593ef9");
        Assert.Equal("41d337593ef9", hash);
    }

    [Fact]
    public void ExtractHashFromSlug_LongSlug()
    {
        var hash = ShopUrlHelper.ExtractHashFromSlug(
            "cream-cash-rules-everything-around-me-0ee0311806ea");
        Assert.Equal("0ee0311806ea", hash);
    }

    [Fact]
    public void ExtractHashFromSlug_TooShort_ReturnsNull()
    {
        var hash = ShopUrlHelper.ExtractHashFromSlug("abc");
        Assert.Null(hash);
    }

    [Fact]
    public void ExtractHashFromSlug_NoHash_ReturnsNull()
    {
        var hash = ShopUrlHelper.ExtractHashFromSlug("no-hex-here-zzzzzzZZZZZZ");
        Assert.Null(hash);
    }

    // ─── Slugify ────────────────────────────────────────

    [Theory]
    [InlineData("Dream On", "dream-on")]
    [InlineData("Flowers", "flowers")]
    [InlineData("Moves Like Jagger", "moves-like-jagger")]
    [InlineData("Sweet Child O' Mine", "sweet-child-o-mine")]
    [InlineData("The Middle", "the-middle")]
    [InlineData("WANNABE", "wannabe")]
    [InlineData("tv off", "tv-off")]
    public void Slugify_ProducesExpectedSlugs(string title, string expectedSlug)
    {
        var slug = ShopUrlHelper.Slugify(title);
        Assert.Equal(expectedSlug, slug);
    }

    [Fact]
    public void Slugify_StripsPeriodsAndParentheses()
    {
        // "C.R.E.A.M. (Cash Rules Everything Around Me)" should have dots/parens stripped
        var slug = ShopUrlHelper.Slugify("C.R.E.A.M. (Cash Rules Everything Around Me)");
        Assert.Equal("cream-cash-rules-everything-around-me", slug);
    }

    [Fact]
    public void Slugify_HandlesExclamation()
    {
        var slug = ShopUrlHelper.Slugify("HOT TO GO!");
        Assert.Equal("hot-to-go", slug);
    }

    [Fact]
    public void Slugify_DropsHyphensInTitle()
    {
        var slug = ShopUrlHelper.Slugify("Bling-Bang-Bang-Born");
        Assert.Equal("blingbangbangborn", slug);
    }

    [Fact]
    public void Slugify_DropsHyphenBetweenDigitAndWord()
    {
        // "8-Bit Beat" → hyphen dropped, space becomes hyphen
        var slug = ShopUrlHelper.Slugify("8-Bit Beat");
        Assert.Equal("8bit-beat", slug);
    }

    [Fact]
    public void Slugify_HyphenSurroundedBySpaces()
    {
        // "Undone - The Sweater Song" → spaces become hyphens, hyphen dropped, collapses
        var slug = ShopUrlHelper.Slugify("Undone - The Sweater Song");
        Assert.Equal("undone-the-sweater-song", slug);
    }
}
