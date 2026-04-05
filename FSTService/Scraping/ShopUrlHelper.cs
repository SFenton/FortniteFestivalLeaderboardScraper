using System.Text;
using System.Text.RegularExpressions;

namespace FSTService.Scraping;

/// <summary>
/// Computes Fortnite Item Shop deep-link URLs for Jam Tracks.
/// The URL hash is the last 12 hex characters of the song's UUID (track.su).
/// </summary>
public static partial class ShopUrlHelper
{
    private const string ShopBaseUrl = "https://www.fortnite.com/item-shop/jam-tracks/";

    /// <summary>
    /// Computes the fortnite.com Item Shop URL for a Jam Track.
    /// </summary>
    /// <param name="songId">The song UUID (track.su), e.g. "9b468bdf-3379-4297-b1f3-41d337593ef9".</param>
    /// <param name="title">The song title (track.tt), e.g. "Dream On".</param>
    /// <returns>Full URL like "https://www.fortnite.com/item-shop/jam-tracks/dream-on-41d337593ef9".</returns>
    public static string ComputeShopUrl(string songId, string title)
    {
        var slug = Slugify(title);
        var hash = ExtractHash(songId);
        return $"{ShopBaseUrl}{slug}-{hash}";
    }

    /// <summary>
    /// Extracts the 12-character hex hash from a song UUID.
    /// This is the last 12 characters of the UUID with dashes removed.
    /// </summary>
    public static string ExtractHash(string songId)
    {
        var noDashes = songId.Replace("-", "");
        return noDashes.Length >= 12
            ? noDashes[^12..]
            : noDashes;
    }

    /// <summary>
    /// Extracts the trailing 12-char hex hash from a shop URL slug.
    /// E.g. "dream-on-41d337593ef9" → "41d337593ef9".
    /// Returns null if the slug doesn't end with a valid 12-char hex suffix.
    /// </summary>
    public static string? ExtractHashFromSlug(string slug)
    {
        if (slug.Length < 13) return null; // at minimum: "x-" + 12 hex chars

        var lastDash = slug.LastIndexOf('-');
        if (lastDash < 0 || lastDash >= slug.Length - 1) return null;

        var candidate = slug[(lastDash + 1)..];
        if (candidate.Length != 12) return null;

        return HexPattern().IsMatch(candidate) ? candidate : null;
    }

    /// <summary>
    /// Converts a song title to a URL-safe slug.
    /// E.g. "Dream On" → "dream-on", "C.R.E.A.M. (Cash Rules Everything Around Me)" → "cream-cash-rules-everything-around-me".
    /// </summary>
    internal static string Slugify(string title)
    {
        var sb = new StringBuilder(title.Length);
        foreach (var c in title)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
            else if (c is ' ' or '_')
                sb.Append('-');
            // Other characters (hyphens, punctuation, apostrophes, etc.) are dropped
        }

        // Collapse multiple consecutive hyphens and trim leading hyphens only
        // (trailing hyphens from trailing spaces in titles must be preserved for correct URLs)
        var result = MultipleHyphens().Replace(sb.ToString(), "-").TrimStart('-');
        return result;
    }

    [GeneratedRegex(@"-{2,}")]
    private static partial Regex MultipleHyphens();

    [GeneratedRegex(@"^[0-9a-f]{12}$")]
    private static partial Regex HexPattern();
}
