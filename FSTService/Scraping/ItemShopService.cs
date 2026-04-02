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
    IReadOnlySet<string> LeavingTomorrowSongIds { get; }
}

/// <summary>Entry extracted from the fortnite-api.com shop JSON for a single jam track.</summary>
internal readonly record struct ShopTrackEntry(string Title, DateTime? OutDate);

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
    private readonly IMetaDatabase _metaDb;
    private readonly ILogger<ItemShopService> _log;
    private NotificationService? _notifications;
    private FSTService.Api.ShopCacheService? _shopCache;
    private System.Text.Json.JsonSerializerOptions? _jsonOpts;

    private HashSet<string> _inShopSongIds = new();
    private HashSet<string> _leavingTomorrowSongIds = new();
    private string? _lastContentHash;
    private DateTime? _lastScrapedAt;
    private Timer? _midnightTimer;
    private readonly object _lock = new();

    /// <summary>The set of songIds currently in the Item Shop.</summary>
    public IReadOnlySet<string> InShopSongIds
    {
        get { lock (_lock) return _inShopSongIds; }
    }

    /// <summary>The subset of in-shop songIds whose offer expires tomorrow (UTC).</summary>
    public IReadOnlySet<string> LeavingTomorrowSongIds
    {
        get { lock (_lock) return _leavingTomorrowSongIds; }
    }

    /// <summary>When the last successful scrape completed (UTC).</summary>
    public DateTime? LastScrapedAt
    {
        get { lock (_lock) return _lastScrapedAt; }
    }

    public ItemShopService(
        HttpClient http,
        FestivalService festivalService,
        IMetaDatabase metaDb,
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
    public void SetShopCacheService(FSTService.Api.ShopCacheService shopCache) => _shopCache = shopCache;
    public void SetJsonSerializerOptions(System.Text.Json.JsonSerializerOptions jsonOpts) => _jsonOpts = jsonOpts;

    // ─── Initialization ─────────────────────────────────────────

    /// <summary>
    /// Loads persisted shop data from DB, then kicks off an async scrape.
    /// Call after FestivalService and IMetaDatabase are initialized.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // Load stale-but-valid data from DB to serve immediately
        var (persisted, persistedLeaving) = _metaDb.LoadItemShopTracks();
        if (persisted.Count > 0)
        {
            lock (_lock)
            {
                _inShopSongIds = persisted;
                _leavingTomorrowSongIds = persistedLeaving;
                _log.LogInformation("Loaded {Count} in-shop songs from DB ({Leaving} leaving tomorrow).",
                    persisted.Count, persistedLeaving.Count);
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

        // Parse the API response for jam track entries (title + outDate)
        var entries = ExtractJamTrackEntries(json);
        var titles = entries.Select(e => e.Title).ToList();
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
        HashSet<string> previousLeaving;
        lock (_lock)
        {
            previousIds = _inShopSongIds;
            previousLeaving = _leavingTomorrowSongIds;
        }
        var added = matched.Except(previousIds).ToList();
        var removed = previousIds.Except(matched).ToList();

        // Compute leaving-tomorrow set from outDate
        var leavingTomorrow = ComputeLeavingTomorrow(entries, matched);
        var leavingChanged = !leavingTomorrow.SetEquals(previousLeaving);

        // Update state
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            _inShopSongIds = matched;
            _leavingTomorrowSongIds = leavingTomorrow;
            _lastContentHash = contentHash;
            _lastScrapedAt = now;
        }

        // Persist to DB
        _metaDb.SaveItemShopTracks(matched, leavingTomorrow, now);

        // Prime the shop cache so /api/shop serves instantly
        PrimeShopCache(matched, leavingTomorrow);

        // Broadcast shop change to all connected WebSocket clients
        if (_notifications is not null && (added.Count > 0 || removed.Count > 0 || leavingChanged))
        {
            try
            {
                var addedEnriched = FSTService.Api.ShopCacheService.BuildEnrichedSongList(
                    added, leavingTomorrow, _festivalService);
                await _notifications.NotifyShopChangedAsync(addedEnriched, removed, matched.Count, leavingTomorrow);
                _log.LogInformation(
                    "Shop change broadcast: {Added} added, {Removed} removed, leaving changed: {LeavingChanged}.",
                    added.Count, removed.Count, leavingChanged);
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
        return ExtractJamTrackEntries(json).Select(e => e.Title).ToList();
    }

    /// <summary>
    /// Extracts Jam Track entries (title + outDate) from the fortnite-api.com /v2/shop JSON response.
    /// Each shop entry has an <c>outDate</c> that applies to all tracks within it.
    /// </summary>
    internal static List<ShopTrackEntry> ExtractJamTrackEntries(string json)
    {
        var result = new List<ShopTrackEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return result;
            if (!data.TryGetProperty("entries", out var entries)) return result;

            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("tracks", out var tracks)) continue;

                DateTime? outDate = null;
                if (entry.TryGetProperty("outDate", out var outDateProp) &&
                    outDateProp.GetString() is { Length: > 0 } outDateStr &&
                    DateTime.TryParse(outDateStr, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var parsedOutDate))
                {
                    outDate = parsedOutDate;
                }

                foreach (var track in tracks.EnumerateArray())
                {
                    if (track.TryGetProperty("title", out var title) &&
                        title.GetString() is { Length: > 0 } t &&
                        seen.Add(t))
                    {
                        result.Add(new ShopTrackEntry(t, outDate));
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Malformed JSON — return empty
        }
        return result;
    }

    /// <summary>
    /// Given the parsed shop entries and matched songIds, returns the set of songIds
    /// whose offer ends today (outDate falls on today's date in UTC), meaning they
    /// will no longer be available tomorrow.
    /// </summary>
    internal static HashSet<string> ComputeLeavingTomorrow(
        List<ShopTrackEntry> entries,
        HashSet<string> matchedSongIds,
        Dictionary<string, string> titleToSongId,
        DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var today = now.Date;
        var leaving = new HashSet<string>();

        foreach (var entry in entries)
        {
            if (!entry.OutDate.HasValue) continue;
            if (entry.OutDate.Value.Date != today) continue;
            if (titleToSongId.TryGetValue(entry.Title, out var songId) &&
                matchedSongIds.Contains(songId))
            {
                leaving.Add(songId);
            }
        }

        return leaving;
    }

    /// <summary>
    /// Instance overload that resolves title→songId from the FestivalService catalog.
    /// </summary>
    private HashSet<string> ComputeLeavingTomorrow(
        List<ShopTrackEntry> entries,
        HashSet<string> matchedSongIds)
    {
        var titleToSongId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var song in _festivalService.Songs)
        {
            if (song.track?.tt is not null && song.track.su is not null)
                titleToSongId.TryAdd(song.track.tt, song.track.su);
        }

        return ComputeLeavingTomorrow(entries, matchedSongIds, titleToSongId);
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

        // Recompute leaving-tomorrow since the date boundary shifted,
        // even before we detect any content change.
        await RecomputeLeavingTomorrowAsync();

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

    // ─── Leaving-Tomorrow Recompute ────────────────────────────

    /// <summary>
    /// Re-fetches the shop JSON solely to recompute the leaving-tomorrow set
    /// (the date boundary has shifted at midnight). Broadcasts the updated set
    /// even if the shop content hasn't changed.
    /// </summary>
    private async Task RecomputeLeavingTomorrowAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, FortniteApiShopUrl);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _http.SendAsync(request, CancellationToken.None);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(CancellationToken.None);

            var entries = ExtractJamTrackEntries(json);
            HashSet<string> currentIds;
            lock (_lock) currentIds = _inShopSongIds;

            var leavingTomorrow = ComputeLeavingTomorrow(entries, currentIds);
            HashSet<string> previousLeaving;
            lock (_lock)
            {
                previousLeaving = _leavingTomorrowSongIds;
                _leavingTomorrowSongIds = leavingTomorrow;
            }

            if (!leavingTomorrow.SetEquals(previousLeaving))
            {
                _metaDb.SaveItemShopTracks(currentIds, leavingTomorrow, DateTime.UtcNow);
                PrimeShopCache(currentIds, leavingTomorrow);

                if (_notifications is not null)
                {
                    await _notifications.NotifyShopChangedAsync([], [], currentIds.Count, leavingTomorrow);
                    _log.LogInformation("Leaving-tomorrow set updated at midnight: {Count} songs.", leavingTomorrow.Count);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to recompute leaving-tomorrow set at midnight.");
        }
    }

    // ─── Shop Cache Priming ────────────────────────────────────

    private void PrimeShopCache(IReadOnlySet<string> inShop, IReadOnlySet<string> leaving)
    {
        if (_shopCache is null || _jsonOpts is null) return;
        try
        {
            _shopCache.Prime(inShop, leaving, _festivalService, _jsonOpts);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to prime shop cache.");
        }
    }

    // ─── Regex ──────────────────────────────────────────────────

    [GeneratedRegex(@"/item-shop/jam-tracks/([a-z0-9\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex JamTrackHrefPattern();
}
