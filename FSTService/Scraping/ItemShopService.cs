using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FortniteFestival.Core.Services;
using FSTService.Api;
using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// Provides read-only access to the current in-shop song set.
/// Used by NotificationService to send shop snapshots on WebSocket connect.
/// </summary>
public interface IShopProvider
{
    IReadOnlySet<string> InShopSongIds { get; }
}

/// <summary>
/// Scrapes the Fortnite Item Shop Jam Tracks page to determine which songs
/// are currently available for purchase, and provides in-memory lookup of
/// the current in-shop set.
/// </summary>
public sealed partial class ItemShopService : IShopProvider
{
    private const string FortniteApiShopUrl = "https://fortnite-api.com/v2/shop";
    private const int MidnightRetryIntervalMs = 15_000;
    private const int MidnightMaxRetries = 40; // ~10 minutes

    private readonly HttpClient _http;
    private readonly FestivalService _festivalService;
    private readonly MetaDatabase _metaDb;
    private readonly ILogger<ItemShopService> _log;
    private NotificationService? _notifications;

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

    /// <summary>
    /// Wire up the notification service for broadcasting shop changes.
    /// Called during startup to break the circular dependency.
    /// </summary>
    public void SetNotificationService(NotificationService notifications) => _notifications = notifications;

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
    /// Fetches the Item Shop from fortnite-api.com, matches Jam Track titles to the
    /// song catalog, and updates the in-memory set + DB.
    /// Returns the count of matched songs, or -1 if content was unchanged.
    /// </summary>
    public async Task<int> ScrapeAsync(CancellationToken ct = default)
    {
        _log.LogInformation("Fetching Item Shop Jam Tracks from fortnite-api.com...");

        using var request = new HttpRequestMessage(HttpMethod.Get, FortniteApiShopUrl);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);

        // Parse the API response for jam track titles
        var titles = ExtractJamTrackTitles(json);
        if (titles.Count == 0)
        {
            _log.LogWarning("No Jam Tracks found in fortnite-api.com shop response.");
            return 0;
        }

        // Content change detection
        var contentHash = ComputeContentHash(titles);
        if (contentHash == _lastContentHash)
        {
            _log.LogDebug("Shop content unchanged ({Count} tracks).", titles.Count);
            return -1;
        }

        // Match titles to song catalog
        var matched = MatchTitlesToSongs(titles);

        // If we have unmatched titles, try refreshing the song catalog
        if (matched.Count < titles.Count)
        {
            var unmatchedCount = titles.Count - matched.Count;
            _log.LogInformation(
                "{Unmatched} shop tracks unmatched. Syncing song catalog...",
                unmatchedCount);

            await _festivalService.SyncSongsAsync();
            matched = MatchTitlesToSongs(titles);

            var stillUnmatched = titles.Count - matched.Count;
            if (stillUnmatched > 0)
            {
                var matchedTitles = new HashSet<string>(
                    _festivalService.Songs
                        .Where(s => s.track?.tt is not null && matched.Contains(s.track.su))
                        .Select(s => s.track.tt!),
                    StringComparer.OrdinalIgnoreCase);
                var unmatched = titles.Where(t => !matchedTitles.Contains(t)).ToList();
                _log.LogWarning(
                    "{Count} shop tracks still unmatched after catalog sync: {Titles}",
                    stillUnmatched, string.Join(", ", unmatched));
            }
        }

        // Compute diff before updating state
        HashSet<string> previousIds;
        lock (_lock)
        {
            previousIds = _inShopSongIds;
        }
        var added = matched.Except(previousIds).ToList();
        var removed = previousIds.Except(matched).ToList();

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

        // Broadcast shop change to all connected WebSocket clients
        if (_notifications is not null && (added.Count > 0 || removed.Count > 0))
        {
            try
            {
                await _notifications.NotifyShopChangedAsync(added, removed, matched.Count);
                _log.LogInformation(
                    "Shop change broadcast: {Added} added, {Removed} removed.",
                    added.Count, removed.Count);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to broadcast shop change notification.");
            }
        }

        _log.LogInformation(
            "Item Shop update complete: {Matched}/{Total} tracks matched.",
            matched.Count, titles.Count);

        return matched.Count;
    }

    /// <summary>
    /// Triggers a manual scrape. Returns the result count.
    /// </summary>
    public Task<int> TriggerScrapeAsync(CancellationToken ct = default)
        => ScrapeAsync(ct);

    // ─── Matching ───────────────────────────────────────────────

    private HashSet<string> MatchTitlesToSongs(List<string> titles)
    {
        // Build case-insensitive title → songId lookup
        var titleToSong = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var song in _festivalService.Songs)
        {
            if (song.track?.tt is not null && song.track.su is not null)
                titleToSong.TryAdd(song.track.tt, song.track.su);
        }

        var matched = new HashSet<string>();
        foreach (var title in titles)
        {
            if (titleToSong.TryGetValue(title, out var songId))
                matched.Add(songId);
        }

        return matched;
    }

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

    // ─── JSON Parsing (fortnite-api.com) ───────────────────────

    /// <summary>
    /// Extracts Jam Track titles from the fortnite-api.com /v2/shop JSON response.
    /// </summary>
    internal static List<string> ExtractJamTrackTitles(string json)
    {
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return [];
            if (!data.TryGetProperty("entries", out var entries)) return [];

            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("tracks", out var tracks)) continue;
                foreach (var track in tracks.EnumerateArray())
                {
                    if (track.TryGetProperty("title", out var title) &&
                        title.GetString() is { Length: > 0 } t)
                    {
                        titles.Add(t);
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Malformed JSON — return empty
        }
        return titles.ToList();
    }

    // ─── HTML Parsing (legacy) ──────────────────────────────────

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
