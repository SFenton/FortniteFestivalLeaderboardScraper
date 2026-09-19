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
    IReadOnlySet<string> NewSongIds { get; }
}

/// <summary>Entry extracted from the fortnite-api.com shop JSON for a single jam track.</summary>
internal readonly record struct ShopTrackEntry(
    string Title,
    DateTime? OutDate,
    bool IsNew = false,
    DateTime? InDate = null,
    string? TrackId = null);

internal readonly record struct ItemShopScrapeOutcome(
    int MatchedCount,
    int TotalCount,
    int UnmatchedCount,
    bool ContentChanged,
    bool StateChanged,
    bool CandidateStateAccepted,
    bool NotificationsSucceeded,
    long NotificationsInserted)
{
    public bool IsComplete =>
        TotalCount > 0
        && UnmatchedCount == 0
        && CandidateStateAccepted
        && NotificationsSucceeded;

    public int PublicResult =>
        TotalCount == 0
            ? 0
            : ContentChanged || StateChanged || NotificationsInserted > 0
                ? MatchedCount
                : -1;
}

/// <summary>
/// Scrapes the Fortnite Item Shop Jam Tracks page to determine which songs
/// are currently available for purchase, and provides in-memory lookup of
/// the current in-shop set.
/// </summary>
public sealed partial class ItemShopService : IShopProvider, IDisposable
{
    private const string FortniteApiShopUrl = "https://fortnite-api.com/v2/shop";
    private const string SparkTrackTemplatePrefix = "SparksSong:";
    private const int MidnightRetryIntervalMs = 15_000;
    private const int MidnightMaxRetries = 40; // ~10 minutes
    internal static readonly TimeSpan DefaultReconciliationInterval =
        TimeSpan.FromMinutes(15);

    private readonly HttpClient _http;
    private readonly FestivalService _festivalService;
    private readonly IMetaDatabase _metaDb;
    private readonly ImprovementNotificationService? _improvementNotifications;
    private readonly ILogger<ItemShopService> _log;
    private NotificationService? _notifications;
    private FSTService.Api.ShopCacheService? _shopCache;
    private System.Text.Json.JsonSerializerOptions? _jsonOpts;

    private HashSet<string> _inShopSongIds = new();
    private HashSet<string> _leavingTomorrowSongIds = new();
    private HashSet<string> _newSongIds = new();
    private string? _lastContentHash;
    private DateTime? _lastScrapedAt;
    private Timer? _midnightTimer;
    private Timer? _reconciliationTimer;
    private TimeSpan _reconciliationInterval =
        DefaultReconciliationInterval;
    private string? _pendingNegativeFingerprint;
    private readonly SemaphoreSlim _scrapeGate = new(1, 1);
    private readonly object _lock = new();
    private bool _disposed;

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

    /// <summary>The subset of in-shop songIds marked New by the upstream shop API.</summary>
    public IReadOnlySet<string> NewSongIds
    {
        get { lock (_lock) return _newSongIds; }
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
        ImprovementNotificationService? improvementNotifications,
        ILogger<ItemShopService> log)
    {
        _http = http;
        _festivalService = festivalService;
        _metaDb = metaDb;
        _improvementNotifications = improvementNotifications;
        _log = log;
    }

    public ItemShopService(
        HttpClient http,
        FestivalService festivalService,
        IMetaDatabase metaDb,
        ILogger<ItemShopService> log)
        : this(http, festivalService, metaDb, null, log)
    {
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
    public Task InitializeAsync(CancellationToken ct = default)
        => InitializeAsync(DefaultReconciliationInterval, ct);

    public async Task InitializeAsync(
        TimeSpan reconciliationInterval,
        CancellationToken ct = default)
    {
        if (reconciliationInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reconciliationInterval),
                reconciliationInterval,
                "Item Shop reconciliation interval must be positive.");
        }

        _reconciliationInterval = reconciliationInterval;
        LoadPersistedState();

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
        ScheduleReconciliationTimer();
    }

    /// <summary>
    /// Loads and caches existing persisted shop data without HTTP, database writes,
    /// notifications, cleanup, or timer registration.
    /// </summary>
    public Task InitializePersistedStateOnlyAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        LoadPersistedState();
        PrimeShopCache(_inShopSongIds, _leavingTomorrowSongIds, _newSongIds);
        return Task.CompletedTask;
    }

    internal bool HasScheduledRefresh =>
        _midnightTimer is not null && _reconciliationTimer is not null;

    private void LoadPersistedState()
    {
        // Load stale-but-valid data from DB to serve immediately
        var (persisted, persistedLeaving, persistedNew) = _metaDb.LoadItemShopTracks();
        if (persisted.Count > 0)
        {
            lock (_lock)
            {
                _inShopSongIds = persisted;
                _leavingTomorrowSongIds = persistedLeaving;
                _newSongIds = persistedNew;
                _log.LogInformation("Loaded {Count} in-shop songs from DB ({Leaving} leaving tomorrow, {New} new).",
                    persisted.Count, persistedLeaving.Count, persistedNew.Count);
            }
        }
    }

    // ─── Scrape Logic ───────────────────────────────────────────

    /// <summary>
    /// Fetches the Item Shop from fortnite-api.com, matches Jam Tracks to the
    /// song catalog, and updates the in-memory set + DB.
    /// Returns the count of matched songs, or -1 if content was unchanged.
    /// </summary>
    public async Task<int> ScrapeAsync(CancellationToken ct = default)
    {
        await _scrapeGate.WaitAsync(ct);
        try
        {
            var outcome = await ScrapeCoreAsync(
                allowNegativeConfirmation: false,
                ct);
            return outcome.PublicResult;
        }
        finally
        {
            _scrapeGate.Release();
        }
    }

    private async Task<ItemShopScrapeOutcome> ScrapeCoreAsync(
        bool allowNegativeConfirmation,
        CancellationToken ct)
    {
        _log.LogInformation("Fetching Item Shop Jam Tracks from fortnite-api.com...");

        using var request = new HttpRequestMessage(HttpMethod.Get, FortniteApiShopUrl);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);

        // Parse the provider response before deciding whether it changed.
        var entries = ExtractJamTrackEntries(json);
        var now = DateTime.UtcNow;
        var expiredServiceNotificationCount = CleanupExpiredServiceNotifications(now);
        if (entries.Count == 0)
        {
            _log.LogWarning("No Jam Tracks found in fortnite-api.com shop response.");
            await NotifyNotificationFeedChangedIfNeededAsync(0, expiredServiceNotificationCount);
            return new ItemShopScrapeOutcome(
                0,
                0,
                0,
                ContentChanged: false,
                StateChanged: false,
                CandidateStateAccepted: false,
                NotificationsSucceeded: true,
                NotificationsInserted: 0);
        }

        var contentHash = ComputeContentHash(entries);
        var contentChanged = !string.Equals(
            contentHash,
            _lastContentHash,
            StringComparison.Ordinal);

        var matchedEntries = MatchEntriesToSongs(entries);

        // If we have unmatched tracks, try refreshing the song catalog.
        if (matchedEntries.Count < entries.Count)
        {
            var unmatchedCount = entries.Count - matchedEntries.Count;
            _log.LogInformation(
                "{Unmatched} shop tracks unmatched. Syncing song catalog...",
                unmatchedCount);

            await _festivalService.SyncSongsAsync();
            matchedEntries = MatchEntriesToSongs(entries);

            var stillUnmatched = entries.Count - matchedEntries.Count;
            if (stillUnmatched > 0)
            {
                var unmatched = entries
                    .Where(entry =>
                        !matchedEntries.ContainsKey(
                            GetEntryIdentityKey(entry)))
                    .Select(entry => entry.Title)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _log.LogWarning(
                    "{Count} shop tracks still unmatched after catalog sync: {Titles}",
                    stillUnmatched, string.Join(", ", unmatched));
            }
        }

        var matched = matchedEntries.Values
            .Select(song => song.track.su)
            .Where(songId => !string.IsNullOrWhiteSpace(songId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unmatchedAfterSync = entries.Count - matchedEntries.Count;

        HashSet<string> previousIds;
        HashSet<string> previousLeaving;
        HashSet<string> previousNew;
        lock (_lock)
        {
            previousIds = new HashSet<string>(
                _inShopSongIds,
                StringComparer.OrdinalIgnoreCase);
            previousLeaving = new HashSet<string>(
                _leavingTomorrowSongIds,
                StringComparer.OrdinalIgnoreCase);
            previousNew = new HashSet<string>(
                _newSongIds,
                StringComparer.OrdinalIgnoreCase);
        }

        var candidateLeaving = ComputeLeavingTomorrow(
            entries,
            matchedEntries,
            now);
        var candidateNew = ComputeNewSongIds(
            entries,
            matchedEntries);
        var hasNegativeTransition =
            previousIds.Except(matched).Any()
            || previousNew.Except(candidateNew).Any()
            || previousLeaving.Except(candidateLeaving).Any();
        var candidateFingerprint = ComputeDerivedStateHash(
            matched,
            candidateLeaving,
            candidateNew);
        var acceptCandidateState = ShouldAcceptCandidateState(
            hasNegativeTransition,
            unmatchedAfterSync > 0,
            candidateFingerprint,
            allowNegativeConfirmation);

        HashSet<string> effectiveIds;
        HashSet<string> effectiveLeaving;
        HashSet<string> effectiveNew;
        if (acceptCandidateState)
        {
            effectiveIds = matched;
            effectiveLeaving = candidateLeaving;
            effectiveNew = candidateNew;
        }
        else
        {
            effectiveIds = new HashSet<string>(
                previousIds,
                StringComparer.OrdinalIgnoreCase);
            effectiveIds.UnionWith(matched);
            effectiveLeaving = new HashSet<string>(
                previousLeaving,
                StringComparer.OrdinalIgnoreCase);
            effectiveLeaving.UnionWith(candidateLeaving);
            effectiveLeaving.IntersectWith(effectiveIds);
            effectiveNew = new HashSet<string>(
                previousNew,
                StringComparer.OrdinalIgnoreCase);
            effectiveNew.UnionWith(candidateNew);
            effectiveNew.IntersectWith(effectiveIds);
        }

        var added = effectiveIds.Except(previousIds).ToList();
        var removed = previousIds.Except(effectiveIds).ToList();
        var leavingChanged =
            !effectiveLeaving.SetEquals(previousLeaving);
        var newChanged = !effectiveNew.SetEquals(previousNew);
        var stateChanged =
            added.Count > 0
            || removed.Count > 0
            || leavingChanged
            || newChanged;

        var serviceNotificationInputs =
            BuildNewShopSongNotifications(
                entries,
                matchedEntries,
                now);
        var notificationWrite = UpsertNewShopSongNotifications(
            serviceNotificationInputs,
            now);

        lock (_lock)
        {
            _inShopSongIds = effectiveIds;
            _leavingTomorrowSongIds = effectiveLeaving;
            _newSongIds = effectiveNew;
            _lastContentHash = contentHash;
            _lastScrapedAt = now;
        }

        if (stateChanged)
        {
            _metaDb.SaveItemShopTracks(
                effectiveIds,
                effectiveLeaving,
                effectiveNew,
                now);
        }

        if (stateChanged || contentChanged)
        {
            PrimeShopCache(
                effectiveIds,
                effectiveLeaving,
                effectiveNew);
        }

        // Broadcast shop change to all connected WebSocket clients
        if (_notifications is not null && stateChanged)
        {
            try
            {
                var addedEnriched = FSTService.Api.ShopCacheService.BuildEnrichedSongList(
                    added,
                    effectiveLeaving,
                    effectiveNew,
                    _festivalService);
                await _notifications.NotifyShopChangedAsync(
                    addedEnriched,
                    removed,
                    effectiveIds.Count,
                    effectiveLeaving,
                    effectiveNew);
                _log.LogInformation(
                    "Shop change broadcast: {Added} added, {Removed} removed, leaving changed: {LeavingChanged}, new changed: {NewChanged}.",
                    added.Count, removed.Count, leavingChanged, newChanged);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to broadcast shop change notification.");
            }
        }

        await NotifyNotificationFeedChangedIfNeededAsync(
            notificationWrite.Inserted,
            expiredServiceNotificationCount);

        _log.LogInformation(
            "Item Shop reconciliation complete: {Matched}/{Total} tracks matched; contentChanged={ContentChanged}, stateChanged={StateChanged}, notificationsSucceeded={NotificationsSucceeded}.",
            matched.Count,
            entries.Count,
            contentChanged,
            stateChanged,
            notificationWrite.Succeeded);

        return new ItemShopScrapeOutcome(
            matched.Count,
            entries.Count,
            unmatchedAfterSync,
            contentChanged,
            stateChanged,
            acceptCandidateState,
            notificationWrite.Succeeded,
            notificationWrite.Inserted);
    }

    /// <summary>
    /// Triggers a manual scrape. Returns the result count.
    /// </summary>
    public Task<int> TriggerScrapeAsync(CancellationToken ct = default)
        => ScrapeAsync(ct);

    // ─── Matching ───────────────────────────────────────────────

    private Dictionary<string, FortniteFestival.Core.Song>
        MatchEntriesToSongs(IReadOnlyList<ShopTrackEntry> entries)
    {
        var ambiguousEntryTitles = entries
            .GroupBy(
                entry => entry.Title,
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group
                .Select(GetEntryIdentityKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Skip(1)
                .Any())
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var trackIdToSong =
            new Dictionary<string, FortniteFestival.Core.Song>(
                StringComparer.OrdinalIgnoreCase);
        var titleToSong =
            new Dictionary<string, FortniteFestival.Core.Song?>(
                StringComparer.OrdinalIgnoreCase);
        foreach (var song in _festivalService.Songs)
        {
            if (song.track?.su is null)
                continue;

            if (!string.IsNullOrWhiteSpace(song.track.ti))
            {
                trackIdToSong.TryAdd(song.track.ti, song);
                if (song.track.ti.StartsWith(
                        SparkTrackTemplatePrefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    trackIdToSong.TryAdd(
                        song.track.ti[SparkTrackTemplatePrefix.Length..],
                        song);
                }
            }

            if (string.IsNullOrWhiteSpace(song.track.tt))
                continue;

            if (!titleToSong.TryAdd(song.track.tt, song)
                && titleToSong[song.track.tt]?.track.su
                    != song.track.su)
            {
                titleToSong[song.track.tt] = null;
            }
        }

        var matched =
            new Dictionary<string, FortniteFestival.Core.Song>(
                StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            FortniteFestival.Core.Song? song = null;
            if (!string.IsNullOrWhiteSpace(entry.TrackId))
            {
                trackIdToSong.TryGetValue(entry.TrackId, out song);
            }

            if (song is null
                && !ambiguousEntryTitles.Contains(entry.Title)
                && titleToSong.TryGetValue(entry.Title, out var titleMatch))
            {
                if (string.IsNullOrWhiteSpace(entry.TrackId)
                    || string.IsNullOrWhiteSpace(titleMatch?.track?.ti))
                {
                    song = titleMatch;
                }
            }

            if (song is not null)
                matched[GetEntryIdentityKey(entry)] = song;
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
    /// Extracts Jam Track entries from the fortnite-api.com /v2/shop JSON
    /// response. Offer-level dates and banners apply to every contained track.
    /// </summary>
    internal static List<ShopTrackEntry> ExtractJamTrackEntries(string json)
    {
        var result =
            new Dictionary<string, ShopTrackEntry>(
                StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data))
                return [];
            if (!data.TryGetProperty("entries", out var entries))
                return [];

            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("tracks", out var tracks)) continue;

                DateTime? outDate = null;
                DateTime? inDate = null;
                var isNew = false;
                if (entry.TryGetProperty("outDate", out var outDateProp) &&
                    outDateProp.GetString() is { Length: > 0 } outDateStr &&
                    DateTime.TryParse(outDateStr, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var parsedOutDate))
                {
                    outDate = parsedOutDate;
                }

                if (entry.TryGetProperty("inDate", out var inDateProp) &&
                    inDateProp.GetString() is { Length: > 0 } inDateStr &&
                    DateTime.TryParse(inDateStr, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var parsedInDate))
                {
                    inDate = parsedInDate;
                }

                if (entry.TryGetProperty("banner", out var banner))
                {
                    isNew = IsNewBannerValue(banner, "value") || IsNewBannerValue(banner, "backendValue");
                }

                foreach (var track in tracks.EnumerateArray())
                {
                    if (track.TryGetProperty("title", out var title) &&
                        title.GetString() is { Length: > 0 } t)
                    {
                        var trackId =
                            track.TryGetProperty("id", out var id)
                                ? id.GetString()
                                : null;
                        var candidate = new ShopTrackEntry(
                            t,
                            outDate,
                            isNew,
                            inDate,
                            trackId);
                        var key = GetEntryIdentityKey(candidate);
                        if (result.TryGetValue(key, out var current))
                            result[key] = MergeEntries(current, candidate);
                        else
                            result[key] = candidate;
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Malformed JSON — return empty
        }
        return result.Values.ToList();
    }

    private static ShopTrackEntry MergeEntries(
        ShopTrackEntry current,
        ShopTrackEntry candidate)
    {
        var mergedIsNew = current.IsNew || candidate.IsNew;
        DateTime? mergedInDate;
        if (candidate.IsNew)
        {
            mergedInDate = current.IsNew
                ? Latest(current.InDate, candidate.InDate)
                : candidate.InDate ?? current.InDate;
        }
        else
        {
            mergedInDate = Latest(
                current.InDate,
                candidate.InDate);
        }

        return current with
        {
            OutDate = LatestKnownEnd(current.OutDate, candidate.OutDate),
            IsNew = mergedIsNew,
            InDate = mergedInDate,
            TrackId = current.TrackId ?? candidate.TrackId,
        };
    }

    private static DateTime? Latest(
        DateTime? first,
        DateTime? second)
    {
        if (!first.HasValue)
            return second;
        if (!second.HasValue)
            return first;
        return first.Value >= second.Value ? first : second;
    }

    private static DateTime? LatestKnownEnd(
        DateTime? first,
        DateTime? second)
    {
        if (!first.HasValue || !second.HasValue)
            return null;
        return first.Value >= second.Value ? first : second;
    }

    private static string GetEntryIdentityKey(ShopTrackEntry entry)
        => !string.IsNullOrWhiteSpace(entry.TrackId)
            ? $"id:{entry.TrackId}"
            : $"title:{entry.Title}";

    private static bool IsNewBannerValue(System.Text.Json.JsonElement banner, string propertyName)
    {
        return banner.TryGetProperty(propertyName, out var value) &&
            string.Equals(value.GetString(), "New", StringComparison.OrdinalIgnoreCase);
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

    internal static HashSet<string> ComputeNewSongIds(
        List<ShopTrackEntry> entries,
        HashSet<string> matchedSongIds,
        Dictionary<string, string> titleToSongId)
    {
        var newSongIds = new HashSet<string>();

        foreach (var entry in entries)
        {
            if (!entry.IsNew) continue;
            if (titleToSongId.TryGetValue(entry.Title, out var songId) &&
                matchedSongIds.Contains(songId))
            {
                newSongIds.Add(songId);
            }
        }

        return newSongIds;
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

    private static string ComputeContentHash(List<ShopTrackEntry> entries)
    {
        var sorted = entries
            .Select(e => string.Concat(
                GetEntryIdentityKey(e),
                '\t',
                e.Title,
                '\t',
                e.InDate?.ToString("O") ?? "",
                '\t',
                e.OutDate?.ToString("O") ?? "",
                '\t',
                e.IsNew ? "1" : "0"))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        var combined = string.Join('\n', sorted);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(bytes);
    }

    private static string ComputeDerivedStateHash(
        IReadOnlySet<string> songIds,
        IReadOnlySet<string> leavingTomorrow,
        IReadOnlySet<string> newSongIds)
    {
        var rows = songIds
            .OrderBy(songId => songId, StringComparer.Ordinal)
            .Select(songId => string.Concat(
                songId,
                '\t',
                leavingTomorrow.Contains(songId) ? "1" : "0",
                '\t',
                newSongIds.Contains(songId) ? "1" : "0"));
        return Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join('\n', rows))));
    }

    private bool ShouldAcceptCandidateState(
        bool hasNegativeTransition,
        bool hasUnmatchedEntries,
        string candidateFingerprint,
        bool allowNegativeConfirmation)
    {
        if (!hasNegativeTransition)
        {
            _pendingNegativeFingerprint = null;
            return true;
        }

        if (hasUnmatchedEntries)
        {
            _pendingNegativeFingerprint = candidateFingerprint;
            return false;
        }

        if (allowNegativeConfirmation
            && string.Equals(
                _pendingNegativeFingerprint,
                candidateFingerprint,
                StringComparison.Ordinal))
        {
            _pendingNegativeFingerprint = null;
            return true;
        }

        _pendingNegativeFingerprint = candidateFingerprint;
        _log.LogWarning(
            "Deferred Item Shop removals or metadata downgrades until a regular reconciliation confirms the same candidate state.");
        return false;
    }

    // ─── Midnight Timer ─────────────────────────────────────────

    private void ScheduleMidnightTimer()
    {
        if (_disposed)
            return;

        var now = DateTime.UtcNow;
        var nextMidnight = now.Date.AddDays(1); // next 00:00 UTC
        var delay = nextMidnight - now;

        _midnightTimer?.Dispose();
        _midnightTimer = new Timer(OnMidnightTimer, null, delay, Timeout.InfiniteTimeSpan);
        _log.LogInformation("Next shop scrape scheduled at {Time} UTC (in {Delay}).",
            nextMidnight.ToString("yyyy-MM-dd HH:mm"), delay);
    }

    private void ScheduleReconciliationTimer()
    {
        if (_disposed)
            return;

        _reconciliationTimer?.Dispose();
        _reconciliationTimer = new Timer(
            OnReconciliationTimer,
            null,
            _reconciliationInterval,
            _reconciliationInterval);
        _log.LogInformation(
            "Item Shop daytime reconciliation scheduled every {Interval}.",
            _reconciliationInterval);
    }

    private async void OnReconciliationTimer(object? state)
    {
        try
        {
            await TryRunScheduledReconciliationAsync(
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Scheduled Item Shop reconciliation failed.");
        }
    }

    internal async Task<bool> TryRunScheduledReconciliationAsync(
        CancellationToken ct = default)
    {
        if (!await _scrapeGate.WaitAsync(0, ct))
        {
            _log.LogDebug(
                "Skipped overlapping Item Shop reconciliation; another refresh is active.");
            return false;
        }

        try
        {
            var outcome = await ScrapeCoreAsync(
                allowNegativeConfirmation: true,
                ct);
            if (!outcome.IsComplete)
            {
                _log.LogWarning(
                    "Item Shop reconciliation remains incomplete: {Unmatched} unmatched tracks; notificationsSucceeded={NotificationsSucceeded}.",
                    outcome.UnmatchedCount,
                    outcome.NotificationsSucceeded);
            }
            return true;
        }
        finally
        {
            _scrapeGate.Release();
        }
    }

    private async void OnMidnightTimer(object? state)
    {
        _log.LogInformation("Midnight UTC — starting shop rotation poll...");

        await _scrapeGate.WaitAsync();
        try
        {
            ItemShopScrapeOutcome? latestOutcome = null;
            var rotationObserved = false;
            for (int attempt = 1; attempt <= MidnightMaxRetries; attempt++)
            {
                try
                {
                    latestOutcome = await ScrapeCoreAsync(
                        allowNegativeConfirmation: attempt > 1,
                        CancellationToken.None);
                    rotationObserved |=
                        latestOutcome.Value.ContentChanged
                        || latestOutcome.Value.StateChanged;
                    if (rotationObserved
                        && latestOutcome.Value.IsComplete)
                    {
                        _log.LogInformation(
                            "Shop rotation detected and reconciled on attempt {Attempt}.",
                            attempt);
                        break;
                    }

                    if (attempt < MidnightMaxRetries)
                    {
                        _log.LogDebug(
                            "Shop rotation not fully reconciled, retrying in {Delay}s (attempt {Attempt}/{Max})...",
                            MidnightRetryIntervalMs / 1000,
                            attempt,
                            MidnightMaxRetries);
                        await Task.Delay(MidnightRetryIntervalMs);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(
                        ex,
                        "Shop scrape failed on attempt {Attempt}.",
                        attempt);
                    if (attempt < MidnightMaxRetries)
                        await Task.Delay(MidnightRetryIntervalMs);
                }
            }

            if (latestOutcome is { IsComplete: false } incomplete)
            {
                _log.LogWarning(
                    "Midnight Item Shop polling ended incomplete: {Unmatched} unmatched tracks; notificationsSucceeded={NotificationsSucceeded}. Daytime reconciliation will retry.",
                    incomplete.UnmatchedCount,
                    incomplete.NotificationsSucceeded);
            }
        }
        finally
        {
            _scrapeGate.Release();
            ScheduleMidnightTimer();
        }
    }

    // ─── Shop Cache Priming ────────────────────────────────────

    private void PrimeShopCache(IReadOnlySet<string> inShop, IReadOnlySet<string> leaving, IReadOnlySet<string> newSongIds)
    {
        if (_shopCache is null || _jsonOpts is null) return;
        try
        {
            _shopCache.Prime(inShop, leaving, newSongIds, _festivalService, _jsonOpts);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to prime shop cache.");
        }
    }

    private IReadOnlyList<NewShopSongServiceNotification> BuildNewShopSongNotifications(
        List<ShopTrackEntry> entries,
        IReadOnlyDictionary<string, FortniteFestival.Core.Song>
            matchedEntries,
        DateTime detectedAtUtc)
    {
        if (_improvementNotifications is null) return [];

        var notifications = new List<NewShopSongServiceNotification>();
        var seenSongIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!entry.IsNew) continue;
            if (!matchedEntries.TryGetValue(
                    GetEntryIdentityKey(entry),
                    out var song)
                || song.track?.su is null)
            {
                continue;
            }
            var songId = song.track.su;
            if (!seenSongIds.Add(songId)) continue;

            notifications.Add(new NewShopSongServiceNotification(
                songId,
                (song.track.tt ?? entry.Title).Trim(),
                (song.track.an ?? "Unknown Artist").Trim(),
                TrimAlbumArt(song.track.au),
                BuildServiceNotificationSourceKey(entry, detectedAtUtc),
                entry.InDate));
        }

        return notifications;
    }

    private (long Inserted, bool Succeeded)
        UpsertNewShopSongNotifications(
        IReadOnlyList<NewShopSongServiceNotification> notifications,
        DateTime detectedAtUtc)
    {
        if (_improvementNotifications is null
            || notifications.Count == 0)
        {
            return (0, true);
        }
        try
        {
            var inserted = _improvementNotifications.UpsertNewShopSongNotifications(notifications, detectedAtUtc);
            if (inserted > 0)
                _log.LogInformation("Inserted {Count} service notification(s) for new Item Shop songs.", inserted);
            return (inserted, true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to insert service notifications for new Item Shop songs.");
            return (0, false);
        }
    }

    private static HashSet<string> ComputeLeavingTomorrow(
        List<ShopTrackEntry> entries,
        IReadOnlyDictionary<string, FortniteFestival.Core.Song>
            matchedEntries,
        DateTime utcNow)
    {
        var leaving = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.OutDate?.Date != utcNow.Date)
                continue;
            if (matchedEntries.TryGetValue(
                    GetEntryIdentityKey(entry),
                    out var song)
                && !string.IsNullOrWhiteSpace(song.track?.su))
            {
                leaving.Add(song.track.su);
            }
        }
        return leaving;
    }

    private static HashSet<string> ComputeNewSongIds(
        List<ShopTrackEntry> entries,
        IReadOnlyDictionary<string, FortniteFestival.Core.Song>
            matchedEntries)
    {
        var newSongIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!entry.IsNew)
                continue;
            if (matchedEntries.TryGetValue(
                    GetEntryIdentityKey(entry),
                    out var song)
                && !string.IsNullOrWhiteSpace(song.track?.su))
            {
                newSongIds.Add(song.track.su);
            }
        }
        return newSongIds;
    }

    private long CleanupExpiredServiceNotifications(DateTime detectedAtUtc)
    {
        if (_improvementNotifications is null) return 0;
        try
        {
            var deleted = _improvementNotifications.CleanupExpiredServiceNotifications(detectedAtUtc);
            if (deleted > 0)
                _log.LogInformation("Deleted {Count} expired service notification(s) during Item Shop poll.", deleted);
            return deleted;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to cleanup expired service notifications during Item Shop poll.");
            return 0;
        }
    }

    private async Task NotifyNotificationFeedChangedIfNeededAsync(long inserted, long deleted)
    {
        if (_notifications is null || inserted <= 0 && deleted <= 0) return;
        try
        {
            await _notifications.NotifyNotificationFeedChangedAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to broadcast service notification feed change.");
        }
    }

    private static string BuildServiceNotificationSourceKey(ShopTrackEntry entry, DateTime detectedAtUtc)
        => entry.InDate is { } inDate
            ? $"in:{inDate.ToUniversalTime():O}"
            : $"detected-day:{detectedAtUtc:yyyy-MM-dd}";

    private static string? TrimAlbumArt(string? url)
        => url is not null && url.StartsWith(ApiEndpoints.AlbumArtPrefix, StringComparison.Ordinal)
            ? url[ApiEndpoints.AlbumArtPrefix.Length..]
            : url;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _midnightTimer?.Dispose();
        _reconciliationTimer?.Dispose();
    }

    // ─── Regex ──────────────────────────────────────────────────

    [GeneratedRegex(@"/item-shop/jam-tracks/([a-z0-9\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex JamTrackHrefPattern();
}
