using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FortniteFestival.Core.Services;
using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// Scrapes the Fortnite Item Shop Jam Tracks page to determine which songs
/// are currently available for purchase, and provides in-memory lookup of
/// the current in-shop set.
/// </summary>
public sealed partial class ItemShopService
{
    private const string JamTracksUrl = "https://www.fortnite.com/item-shop/jam-tracks?lang=en-US";
    private const int MidnightRetryIntervalMs = 15_000;
    private const int MidnightMaxRetries = 40; // ~10 minutes

    private readonly HttpClient _http;
    private readonly FestivalService _festivalService;
    private readonly MetaDatabase _metaDb;
    private readonly ILogger<ItemShopService> _log;

    private HashSet<string> _inShopSongIds = new();
    private string? _lastContentHash;
    private DateTime? _lastScrapedAt;
    private Timer? _midnightTimer;
    private readonly object _lock = new();

    /// <summary>The set of songIds currently in the Item Shop.</summary>
    public IReadOnlySet<string> InShopSongIds
    {
        get { lock (_lock) return _inShopSongIds; }
    }

    /// <summary>When the last successful scrape completed (UTC).</summary>
    public DateTime? LastScrapedAt
    {
        get { lock (_lock) return _lastScrapedAt; }
    }

    public ItemShopService(
        HttpClient http,
        FestivalService festivalService,
        MetaDatabase metaDb,
        ILogger<ItemShopService> log)
    {
        _http = http;
        _festivalService = festivalService;
        _metaDb = metaDb;
        _log = log;
    }

    // ─── Initialization ─────────────────────────────────────────

    /// <summary>
    /// Loads persisted shop data from DB, then kicks off an async scrape.
    /// Call after FestivalService and MetaDatabase are initialized.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // Load stale-but-valid data from DB to serve immediately
        var persisted = _metaDb.LoadItemShopTracks();
        if (persisted.Count > 0)
        {
            lock (_lock)
            {
                _inShopSongIds = persisted;
                _log.LogInformation("Loaded {Count} in-shop songs from DB.", persisted.Count);
            }
        }

        // Scrape for fresh data (best-effort on startup)
        try
        {
            await ScrapeAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Startup shop scrape failed; serving stale data.");
        }

        // Schedule midnight UTC timer
        ScheduleMidnightTimer();
    }

    // ─── Scrape Logic ───────────────────────────────────────────

    /// <summary>
    /// Scrapes the Item Shop page, matches songs, and updates the in-memory set + DB.
    /// Returns the count of matched songs, or -1 if content was unchanged.
    /// </summary>
    public async Task<int> ScrapeAsync(CancellationToken ct = default)
    {
        _log.LogInformation("Scraping Item Shop Jam Tracks...");

        var html = await _http.GetStringAsync(JamTracksUrl, ct);

        // Extract all /item-shop/jam-tracks/{slug} hrefs
        var slugs = ExtractJamTrackSlugs(html);
        if (slugs.Count == 0)
        {
            _log.LogWarning("No Jam Track URLs found on the shop page. Page structure may have changed.");
            return 0;
        }

        // Content change detection
        var contentHash = ComputeContentHash(slugs);
        if (contentHash == _lastContentHash)
        {
            _log.LogDebug("Shop content unchanged ({Count} tracks).", slugs.Count);
            return -1;
        }

        // Extract hashes from slugs
        var shopHashes = new List<(string Slug, string Hash)>();
        foreach (var slug in slugs)
        {
            var hash = ShopUrlHelper.ExtractHashFromSlug(slug);
            if (hash is not null)
                shopHashes.Add((slug, hash));
            else
                _log.LogWarning("Could not extract hash from shop slug: {Slug}", slug);
        }

        // Build hash→songId lookup from catalog
        var matched = MatchHashesToSongs(shopHashes.Select(h => h.Hash).ToList());

        // If we have unmatched hashes, try refreshing the song catalog
        if (matched.Count < shopHashes.Count)
        {
            var unmatchedCount = shopHashes.Count - matched.Count;
            _log.LogInformation(
                "{Unmatched} shop tracks unmatched. Syncing song catalog...",
                unmatchedCount);

            await _festivalService.SyncSongsAsync();

            // Retry matching with refreshed catalog
            matched = MatchHashesToSongs(shopHashes.Select(h => h.Hash).ToList());

            var stillUnmatched = shopHashes.Count - matched.Count;
            if (stillUnmatched > 0)
            {
                _log.LogWarning(
                    "{Count} shop tracks still unmatched after catalog sync.",
                    stillUnmatched);
            }
        }

        // Update state
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            _inShopSongIds = matched;
            _lastContentHash = contentHash;
            _lastScrapedAt = now;
        }

        // Persist to DB
        _metaDb.SaveItemShopTracks(matched, now);

        _log.LogInformation(
            "Item Shop scrape complete: {Matched}/{Total} tracks matched.",
            matched.Count, slugs.Count);

        return matched.Count;
    }

    /// <summary>
    /// Triggers a manual scrape. Returns the result count.
    /// </summary>
    public Task<int> TriggerScrapeAsync(CancellationToken ct = default)
        => ScrapeAsync(ct);

    // ─── Matching ───────────────────────────────────────────────

    private HashSet<string> MatchHashesToSongs(List<string> hashes)
    {
        // Build a lookup: last12hex → songId
        var hashToSong = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var song in _festivalService.Songs)
        {
            if (song.track?.su is null) continue;
            var h = ShopUrlHelper.ExtractHash(song.track.su);
            hashToSong.TryAdd(h, song.track.su);
        }

        var matched = new HashSet<string>();
        foreach (var hash in hashes)
        {
            if (hashToSong.TryGetValue(hash, out var songId))
                matched.Add(songId);
        }

        return matched;
    }

    // ─── HTML Parsing ───────────────────────────────────────────

    /// <summary>
    /// Extracts Jam Track URL slugs from the shop page HTML.
    /// Matches href values like "/item-shop/jam-tracks/dream-on-41d337593ef9".
    /// </summary>
    internal static List<string> ExtractJamTrackSlugs(string html)
    {
        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in JamTrackHrefPattern().Matches(html))
        {
            var slug = m.Groups[1].Value;
            // Strip query string if present
            var qIdx = slug.IndexOf('?');
            if (qIdx >= 0) slug = slug[..qIdx];
            slugs.Add(slug);
        }
        return slugs.ToList();
    }

    // ─── Content Hashing ────────────────────────────────────────

    private static string ComputeContentHash(List<string> slugs)
    {
        var sorted = slugs.OrderBy(s => s, StringComparer.Ordinal).ToList();
        var combined = string.Join('\n', sorted);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(bytes);
    }

    // ─── Midnight Timer ─────────────────────────────────────────

    private void ScheduleMidnightTimer()
    {
        var now = DateTime.UtcNow;
        var nextMidnight = now.Date.AddDays(1); // next 00:00 UTC
        var delay = nextMidnight - now;

        _midnightTimer?.Dispose();
        _midnightTimer = new Timer(OnMidnightTimer, null, delay, Timeout.InfiniteTimeSpan);
        _log.LogInformation("Next shop scrape scheduled at {Time} UTC (in {Delay}).",
            nextMidnight.ToString("yyyy-MM-dd HH:mm"), delay);
    }

    private async void OnMidnightTimer(object? state)
    {
        _log.LogInformation("Midnight UTC — starting shop rotation poll...");

        for (int attempt = 1; attempt <= MidnightMaxRetries; attempt++)
        {
            try
            {
                var result = await ScrapeAsync(CancellationToken.None);
                if (result >= 0) // content changed (or first scrape)
                {
                    _log.LogInformation("Shop rotation detected on attempt {Attempt}.", attempt);
                    break;
                }

                // Content unchanged — shop hasn't rotated yet
                if (attempt < MidnightMaxRetries)
                {
                    _log.LogDebug("Shop unchanged, retrying in {Delay}s (attempt {Attempt}/{Max})...",
                        MidnightRetryIntervalMs / 1000, attempt, MidnightMaxRetries);
                    await Task.Delay(MidnightRetryIntervalMs);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Shop scrape failed on attempt {Attempt}.", attempt);
                if (attempt < MidnightMaxRetries)
                    await Task.Delay(MidnightRetryIntervalMs);
            }
        }

        // Re-schedule for next midnight
        ScheduleMidnightTimer();
    }

    // ─── Regex ──────────────────────────────────────────────────

    [GeneratedRegex(@"/item-shop/jam-tracks/([a-z0-9\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex JamTrackHrefPattern();
}
