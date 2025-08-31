using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FortniteFestival.Core.Auth;
using FortniteFestival.Core.Config;
using FortniteFestival.Core.Persistence;
using FortniteFestival.Core.Services;

namespace FortniteFestival.Core.Services
{
    public class FestivalService : IFestivalService
    {
        private static readonly HttpClient _httpContent = new HttpClient
        {
            BaseAddress = new Uri("https://fortnitecontent-website-prod07.ol.epicgames.com"),
        };
        private static readonly HttpClient _httpEvents = new HttpClient
        {
            BaseAddress = new Uri("https://events-public-service-live.ol.epicgames.com"),
        };
        private static readonly HttpClient _httpAccount = new HttpClient
        {
            BaseAddress = new Uri("https://account-public-service-prod.ol.epicgames.com"),
        };
        private readonly object _sync = new object();
        private readonly ConcurrentDictionary<string, LeaderboardData> _scores =
            new ConcurrentDictionary<string, LeaderboardData>();
        private readonly Dictionary<string, Song> _songs = new Dictionary<string, Song>();
        private List<Song> _songsSnapshot = new List<Song>();
        private bool _songsDirty;
        private bool _initialized;
        private bool _songSyncComplete;
        private volatile bool _authFailed;
        private volatile bool _imagesSyncComplete; // ensure images are downloaded before queries allowed
        private string _imageRoot; // root folder containing images subfolder
        private volatile bool _unauthorizedLogged;
        private int _fetchCompleted;
        private int _fetchTotal; // progress counters
        public bool IsFetching { get; private set; }
        public event Action<string> Log;
        public event Action<string> SongAvailabilityChanged;
        public event Action<LeaderboardData> ScoreUpdated;
        public event Action<int, int, string, bool> SongProgress;
        public IReadOnlyList<Song> Songs
        {
            get
            {
                lock (_sync)
                {
                    if (_songsDirty)
                    {
                        _songsSnapshot = _songs.Values.ToList();
                        _songsDirty = false;
                    }
                    return _songsSnapshot;
                }
            }
        }
        public IReadOnlyDictionary<string, LeaderboardData> ScoresIndex => _scores;
        private readonly IFestivalPersistence _persistence;

        public FestivalService()
            : this(null) { }

        public FestivalService(IFestivalPersistence persistence)
        {
            _persistence = persistence;
        }

        private readonly ConcurrentQueue<string> _logQueue = new ConcurrentQueue<string>();
        private long _lastFlushTicks;

        private void LogLine(string msg)
        {
            var full = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            _logQueue.Enqueue(full);
#if DEBUG
            Debug.WriteLine("[FestivalService] " + full);
#endif
            var now = Stopwatch.GetTimestamp();
            if ((now - Interlocked.Read(ref _lastFlushTicks)) > (Stopwatch.Frequency / 4))
                FlushLogs();
        }

        private void FlushLogs()
        {
            Interlocked.Exchange(ref _lastFlushTicks, Stopwatch.GetTimestamp());
            while (_logQueue.TryDequeue(out var line))
                Log?.Invoke(line);
        }

        public async Task InitializeAsync()
        {
            if (_initialized)
                return;
            _initialized = true;
            if (_persistence != null)
            {
                var loadedScores = await _persistence.LoadScoresAsync().ConfigureAwait(false);
                foreach (var ld in loadedScores)
                {
                    ld.dirty = false;
                    _scores[ld.songId] = ld;
                }
                LogLine($"Loaded {loadedScores.Count} cached scores.");
                var loadedSongs = await _persistence.LoadSongsAsync().ConfigureAwait(false);
                if (loadedSongs != null && loadedSongs.Count > 0)
                {
                    lock (_sync)
                    {
                        foreach (var s in loadedSongs)
                        {
                            if (!_songs.ContainsKey(s.track.su))
                                _songs[s.track.su] = s;
                        }
                        _songsDirty = true;
                    }
                    LogLine($"Loaded {loadedSongs.Count} cached songs.");
                }
            }
            // Establish image root and /images subfolder early
            try
            {
                string rootCandidate = null;
                if (
                    _persistence is FortniteFestival.Core.Persistence.SqlitePersistence sp
                    && !string.IsNullOrEmpty(sp.DatabasePath)
                )
                {
                    var dbDir = System.IO.Path.GetDirectoryName(sp.DatabasePath);
                    if (!string.IsNullOrEmpty(dbDir))
                        rootCandidate = dbDir;
                }
                if (string.IsNullOrEmpty(rootCandidate))
                    rootCandidate = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "FNFestival"
                    );
                if (!System.IO.Directory.Exists(rootCandidate))
                    System.IO.Directory.CreateDirectory(rootCandidate);
                var imagesDir = System.IO.Path.Combine(rootCandidate, "images");
                if (!System.IO.Directory.Exists(imagesDir))
                    System.IO.Directory.CreateDirectory(imagesDir);
                _imageRoot = rootCandidate;
                LogLine($"Image root set to {_imageRoot}\nImages dir={imagesDir}");
            }
            catch (Exception ex)
            {
                LogLine("Image root init failed: " + ex.Message);
            }
            await SyncSongsAsync().ConfigureAwait(false);
            await SyncImagesAsync().ConfigureAwait(false);
            FlushLogs();
        }

        public async Task SyncSongsAsync()
        {
            LogLine("Syncing songs (unauthenticated)...");
            try
            {
                var res = await _httpContent
                    .GetAsync("/content/api/pages/fortnite-game/spark-tracks")
                    .ConfigureAwait(false);
                var content = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    var (ec, msg) = HttpErrorHelper.ExtractError(content);
                    LogLine(HttpErrorHelper.FormatHttpError("SongSync", res, content, ec, msg));
                    return;
                }
                var list = new List<Song>();
                using (var doc = JsonDocument.Parse(content))
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        var elem = prop.Value;
                        if (elem.ValueKind != JsonValueKind.Object)
                            continue;
                        try
                        {
                            string raw = elem.GetRawText(); // naive parse of required fields
                            // simple manual extraction for performance
                            if (raw.IndexOf("\"su\":", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var song = System.Text.Json.JsonSerializer.Deserialize<Song>(raw);
                                if (song != null && song.track != null && song.track.su != null)
                                    list.Add(song);
                            }
                        }
                        catch { }
                    }
                }
                lock (_sync)
                {
                    var incomingIds = new HashSet<string>(list.Select(s => s.track.su));
                    var stale = _songs.Keys.Where(k => !incomingIds.Contains(k)).ToList();
                    foreach (var id in stale)
                        _songs.Remove(id);
                    foreach (var s in list)
                    {
                        if (_songs.TryGetValue(s.track.su, out var existing))
                        {
                            existing.track = s.track;
                            existing._activeDate = s._activeDate;
                            existing.lastModified = s.lastModified;
                            existing._title = s._title ?? existing._title;
                        }
                        else
                            _songs[s.track.su] = s;
                    }
                    _songsDirty = true;
                }
                LogLine($"Song sync complete. {_songs.Count} songs loaded.");
                if (_persistence != null)
                {
                    try
                    {
                        await _persistence.SaveSongsAsync(_songs.Values).ConfigureAwait(false);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                LogLine("Song sync failed: " + ex.Message);
            }
            finally
            {
                _songSyncComplete = true;
                FlushLogs();
            }
        }

        private async Task SyncImagesAsync()
        {
            if (_imagesSyncComplete)
                return;
            if (!_songSyncComplete)
            {
                LogLine("Skipping image sync until songs complete");
                return;
            }
            LogLine("Syncing song images...");
            try
            {
                // Use configured root; fallback if null
                string baseRoot = _imageRoot;
                if (string.IsNullOrEmpty(baseRoot))
                    baseRoot = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "FNFestival"
                    );
                string imagesDir = System.IO.Path.Combine(baseRoot, "images");
                if (!System.IO.Directory.Exists(imagesDir))
                    System.IO.Directory.CreateDirectory(imagesDir);
                var allSongs = Songs;
                int total = allSongs.Count;
                int idx = 0;
                foreach (var s in allSongs)
                {
                    idx++;
                    string display = s._title;
                    SongProgress?.Invoke(idx, total, $"Img {display}", true);
                    bool need = false;
                    string existingPath = s.imagePath;
                    string title = s._title;
                    string safeTitle = title;
                    foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                        safeTitle = safeTitle.Replace(c, '_');
                    string expectedPath = System.IO.Path.Combine(imagesDir, safeTitle + ".jpg");
                    if (string.IsNullOrEmpty(existingPath))
                        need = true;
                    else if (!System.IO.File.Exists(existingPath))
                        need = true;
                    else if (s.lastModified > DateTime.MinValue)
                    {
                        try
                        {
                            var fi = new System.IO.FileInfo(existingPath);
                            if (fi.LastWriteTimeUtc < s.lastModified.ToUniversalTime())
                                need = true;
                        }
                        catch { }
                    }
                    if (!need)
                    {
                        SongProgress?.Invoke(idx, total, $"Img {display}", false);
                        continue;
                    }
                    // ensure expected path uses imagesDir
                    expectedPath = System.IO.Path.Combine(imagesDir, safeTitle + ".jpg");
                    var url = s.track?.au;
                    if (string.IsNullOrEmpty(url))
                    {
                        SongProgress?.Invoke(idx, total, $"Img {display}", false);
                        continue;
                    }
                    try
                    {
                        var imgBytes = await _httpContent
                            .GetByteArrayAsync(url)
                            .ConfigureAwait(false);
                        System.IO.File.WriteAllBytes(expectedPath, imgBytes);
                        s.imagePath = expectedPath;
                    }
                    catch (Exception ex)
                    {
                        LogLine($"Image download failed for {display}: {ex.Message}");
                    }
                    SongProgress?.Invoke(idx, total, $"Img {display}", false);
                }
                if (_persistence != null)
                {
                    try
                    {
                        await _persistence.SaveSongsAsync(_songs.Values).ConfigureAwait(false);
                    }
                    catch { }
                }
                LogLine("Image sync complete.");
            }
            catch (Exception ex)
            {
                LogLine("Image sync failed: " + ex.Message);
            }
            finally
            {
                _imagesSyncComplete = true;
            }
        }

        public Task<bool> FetchScoresAsync(
            string exchangeCode,
            int degreeOfParallelism,
            IList<string> filteredSongIds,
            IEnumerable<InstrumentType> instruments,
            Settings settings
        )
        {
            if (instruments != null)
            {
                var clone = new Settings
                {
                    DegreeOfParallelism = settings?.DegreeOfParallelism ?? degreeOfParallelism,
                };
                foreach (var inst in instruments)
                {
                    switch (inst)
                    {
                        case InstrumentType.Lead:
                            clone.QueryLead = true;
                            break;
                        case InstrumentType.Drums:
                            clone.QueryDrums = true;
                            break;
                        case InstrumentType.Vocals:
                            clone.QueryVocals = true;
                            break;
                        case InstrumentType.Bass:
                            clone.QueryBass = true;
                            break;
                        case InstrumentType.ProLead:
                            clone.QueryProLead = true;
                            break;
                        case InstrumentType.ProBass:
                            clone.QueryProBass = true;
                            break;
                    }
                }
                return FetchScoresAsync(exchangeCode, degreeOfParallelism, filteredSongIds, clone);
            }
            return FetchScoresAsync(exchangeCode, degreeOfParallelism, filteredSongIds, settings);
        }

        // instrumentation counters (reset each fetch session)
        private long _instImproved;
        private long _instEmpty;
        private long _instErrors;
        private long _instRequests;
        private long _instBytes;
        private readonly Stopwatch _runSw = new Stopwatch();

        public (
            long improved,
            long empty,
            long errors,
            long requests,
            long bytes,
            double elapsedSec
        ) GetInstrumentation() =>
            (
                _instImproved,
                _instEmpty,
                _instErrors,
                _instRequests,
                _instBytes,
                _runSw.Elapsed.TotalSeconds
            );

        public async Task<bool> FetchScoresAsync(
            string exchangeCode,
            int degreeOfParallelism,
            IList<string> filteredSongIds,
            Settings settings
        )
        {
            if (IsFetching)
                return false;
            if (!_songSyncComplete || !_imagesSyncComplete)
            {
                LogLine("Initialization (songs/images) not yet complete.");
                return false;
            }
            _authFailed = false;
            _unauthorizedLogged = false;
            IsFetching = true;
            _runSw.Restart();
            _instImproved = 0;
            _instEmpty = 0;
            _instErrors = 0;
            _instRequests = 0;
            _instBytes = 0;
            LogLine("Authenticating...");
            var token = await GetToken(exchangeCode).ConfigureAwait(false);
            if (token == null)
            {
                LogLine("Auth failed (no token). Exchange code may be invalid / already used.");
                IsFetching = false;
                FlushLogs();
                return false;
            }
            // Verify token before heavy work
            if (!await VerifyTokenAsync(token).ConfigureAwait(false))
            {
                LogLine("Token verification failed. Generate a fresh exchange code and retry.");
                IsFetching = false;
                FlushLogs();
                return false;
            }
            var prioritized = Songs
                .Select((s, i) => new { s, i })
                .OrderBy(x => _scores.ContainsKey(x.s.track.su) ? 1 : 0)
                .ThenBy(x => x.i)
                .Select(x => x.s)
                .ToList();
            if (filteredSongIds != null && filteredSongIds.Count > 0)
                prioritized = prioritized.Where(s => filteredSongIds.Contains(s.track.su)).ToList();
            int total = prioritized.Count;
            _fetchTotal = total;
            _fetchCompleted = 0;
            if (total == 0)
            {
                LogLine("No songs selected.");
                IsFetching = false;
                FlushLogs();
                return true;
            }
            int fixedDop =
                settings?.DegreeOfParallelism > 0
                    ? settings.DegreeOfParallelism
                    : Math.Max(1, degreeOfParallelism);
            LogLine(
                $"Fetching leaderboards for {total} songs with fixed concurrency {fixedDop}..."
            );
            var semaphore = new SemaphoreSlim(fixedDop, fixedDop);
            var tasks = new List<Task>();
            foreach (var song in prioritized)
            {
                await semaphore.WaitAsync().ConfigureAwait(false);
                if (_authFailed)
                {
                    semaphore.Release();
                    break;
                }
                var t = Task.Run(async () =>
                {
                    try
                    {
                        await FetchSongAsync(song, token, settings).ConfigureAwait(false);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });
                tasks.Add(t);
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
            if (!_authFailed)
                LogLine(
                    $"Score fetch complete. Improved={_instImproved} Empty={_instEmpty} Errors={_instErrors}"
                );
            else
                LogLine("Fetch aborted due to authorization failure.");
            if (settings != null)
            {
                settings.DegreeOfParallelism = fixedDop;
            }
            if (!_authFailed && _persistence != null)
            {
                try
                {
                    var dirty = _scores.Values.Where(s => s.dirty).ToList();
                    if (dirty.Count > 0)
                    {
                        await _persistence.SaveScoresAsync(dirty).ConfigureAwait(false);
                        foreach (var d in dirty)
                            d.dirty = false;
                        LogLine($"Scores persisted ({dirty.Count} changed).");
                    }
                }
                catch { }
            }
            FlushLogs();
            IsFetching = false;
            _runSw.Stop();
            return !_authFailed;
        }

        private void ReportSongFinished(Song s)
        {
            var done = Interlocked.Increment(ref _fetchCompleted);
            SongProgress?.Invoke(done, _fetchTotal, s.track.tt, false);
        }

        private async Task FetchSongAsync(Song song, ExchangeCodeToken token, Settings settings)
        {
            var instrumentDefs =
                new List<(
                    string api,
                    Func<LeaderboardData, ScoreTracker> getter,
                    Action<LeaderboardData, ScoreTracker> assign,
                    int diff
                )>();
            if (settings == null || settings.QueryDrums)
                instrumentDefs.Add(
                    ("Solo_Drums", l => l.drums, (l, s) => l.drums = s, song.track.@in.ds)
                );
            if (settings == null || settings.QueryLead)
                instrumentDefs.Add(
                    ("Solo_Guitar", l => l.guitar, (l, s) => l.guitar = s, song.track.@in.gr)
                );
            if (settings == null || settings.QueryProBass)
                instrumentDefs.Add(
                    (
                        "Solo_PeripheralBass",
                        l => l.pro_bass,
                        (l, s) => l.pro_bass = s,
                        song.track.@in.pb
                    )
                );
            if (settings == null || settings.QueryProLead)
                instrumentDefs.Add(
                    (
                        "Solo_PeripheralGuitar",
                        l => l.pro_guitar,
                        (l, s) => l.pro_guitar = s,
                        song.track.@in.pg
                    )
                );
            if (settings == null || settings.QueryBass)
                instrumentDefs.Add(
                    ("Solo_Bass", l => l.bass, (l, s) => l.bass = s, song.track.@in.ba)
                );
            if (settings == null || settings.QueryVocals)
                instrumentDefs.Add(
                    ("Solo_Vocals", l => l.vocals, (l, s) => l.vocals = s, song.track.@in.vl)
                );
            if (instrumentDefs.Count == 0)
            {
                ReportSongFinished(song);
                return;
            }
            bool anyImproved = false;
            int emptyCount = 0;
            int errorCount = 0;
            LeaderboardData board = null;
            var tasks = instrumentDefs
                .Select(def =>
                    FetchInstrumentAsync(song, token, def.api, def.diff, def.getter, def.assign)
                )
                .ToArray();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var r in results)
            {
                if (r.status == InstrumentFetchStatus.Improved)
                {
                    anyImproved = true;
                    board = r.board;
                }
                else if (r.status == InstrumentFetchStatus.Empty)
                {
                    emptyCount++;
                }
                else if (r.status == InstrumentFetchStatus.Error)
                {
                    errorCount++;
                }
            }
            if (anyImproved && !_authFailed)
                ScoreUpdated?.Invoke(board);
            // Per song summary log
            LogLine(
                $"Song summary: {song.track.tt} Improved={(anyImproved ? 1 : 0)} Empty={emptyCount} Errors={errorCount}"
            );
            ReportSongFinished(song);
            FlushLogs();
        }

        private enum InstrumentFetchStatus
        {
            None,
            Improved,
            Empty,
            Error,
        }

        private async Task<(
            InstrumentFetchStatus status,
            LeaderboardData board
        )> FetchInstrumentAsync(
            Song song,
            ExchangeCodeToken token,
            string api,
            int diff,
            Func<LeaderboardData, ScoreTracker> getter,
            Action<LeaderboardData, ScoreTracker> assign
        )
        {
            if (_authFailed)
                return (InstrumentFetchStatus.None, null);
            var url =
                $"/api/v2/games/FNFestival/leaderboards/alltime_{song.track.su}_{api}/alltime/scores?accountId={token.account_id}&fromIndex=0&findTeams=false";
            var bodyStr = "{\"teams\":[[\"" + token.account_id + "\"]]}";
            var body = new StringContent(bodyStr, Encoding.UTF8, "application/json");
            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = body };
            req.Headers.Authorization = new AuthenticationHeaderValue("bearer", token.access_token);
            Interlocked.Increment(ref _instRequests);
            Interlocked.Add(ref _instBytes, bodyStr.Length);
            try
            {
                var res = await _httpEvents.SendAsync(req).ConfigureAwait(false);
                var str = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                Interlocked.Add(ref _instBytes, str.Length);
                if (string.IsNullOrEmpty(str))
                    throw new Exception("Empty response");
                ScoreList[] arr = null;
                bool notFound = false;
                string extractedErrorCode = null;
                string extractedErrorMessage = null; // capture error fields
                if (str.TrimStart().StartsWith("{"))
                {
                    // Attempt error field extraction
                    var (ec, msg) = HttpErrorHelper.ExtractError(str);
                    extractedErrorCode = ec;
                    extractedErrorMessage = msg;
                    if (str.IndexOf("errorCode", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var lower = str.ToLowerInvariant();
                        if (lower.Contains("unauthorized"))
                        {
                            _authFailed = true;
                            if (!_unauthorizedLogged)
                            {
                                LogLine(
                                    HttpErrorHelper.FormatHttpError(
                                        $"Leaderboard Unauthorized {song.track.tt} {api}",
                                        res,
                                        str,
                                        extractedErrorCode,
                                        extractedErrorMessage
                                    )
                                );
                                _unauthorizedLogged = true;
                            }
                            Interlocked.Increment(ref _instErrors);
                            LogLine(
                                $"Auth failure counted for {song.track.tt} {api} (errorCode={extractedErrorCode ?? "<none>"})"
                            );
                            return (InstrumentFetchStatus.Error, null);
                        }
                        if (lower.Contains("not_found"))
                        {
                            // treat as empty leaderboard
                            notFound = true;
                            arr = Array.Empty<ScoreList>();
                        }
                        else
                        {
                            Interlocked.Increment(ref _instErrors);
                            LogLine(
                                HttpErrorHelper.FormatHttpError(
                                    $"Leaderboard Error {song.track.tt} {api}",
                                    res,
                                    str,
                                    extractedErrorCode,
                                    extractedErrorMessage
                                )
                            );
                            return (InstrumentFetchStatus.Error, null);
                        }
                    }
                    else
                    {
                        arr = Array.Empty<ScoreList>();
                    }
                }
                else
                {
                    arr = System.Text.Json.JsonSerializer.Deserialize<ScoreList[]>(str);
                }

                if (_authFailed)
                    return (InstrumentFetchStatus.Error, null);
                if (arr != null && arr.Length > 0)
                {
                    var board = _scores.GetOrAdd(
                        song.track.su,
                        _ => new LeaderboardData
                        {
                            songId = song.track.su,
                            title = song.track.tt,
                            artist = song.track.an,
                        }
                    );
                    var tracker = getter(board) ?? new ScoreTracker();
                    tracker.difficulty = diff;
                    bool improved = false;
                    foreach (var sc in arr)
                    {
                        if (sc.score > tracker.maxScore)
                        {
                            tracker.maxScore = sc.score;
                            tracker.initialized = true;
                            improved = true;
                            if (sc.sessionHistory != null)
                            {
                                foreach (var session in sc.sessionHistory)
                                {
                                    if (
                                        session.trackedStats != null
                                        && session.trackedStats.SCORE == sc.score
                                    )
                                    {
                                        tracker.percentHit = session.trackedStats.ACCURACY;
                                        tracker.isFullCombo = session.trackedStats.FULL_COMBO == 1;
                                        tracker.numStars = session.trackedStats.STARS_EARNED;
                                        tracker.seasonAchieved = session.trackedStats.SEASON ?? 0;
                                    }
                                }
                            }
                        }
                    }
                    if (improved)
                    {
                        tracker.RefreshDerived();
                        board.dirty = true;
                        assign(board, tracker);
                        Interlocked.Increment(ref _instImproved);
                        LogLine($"Improved score for {song.track.tt} {api}: {tracker.maxScore}");
                        return (InstrumentFetchStatus.Improved, board);
                    }
                    assign(board, tracker);
                    // existing score unchanged counts as empty (no improvement) but not an error
                    Interlocked.Increment(ref _instEmpty);
                    LogLine($"No improvement for {song.track.tt} {api}");
                    return (InstrumentFetchStatus.Empty, board);
                }
                // empty leaderboard (either truly empty or not_found)
                Interlocked.Increment(ref _instEmpty);
                LogLine(
                    $"No scores for {song.track.tt} {api} {(notFound ? "(not_found)" : "(empty)")} {(extractedErrorCode != null ? "errorCode=" + extractedErrorCode : "")}".Trim()
                );
                return (InstrumentFetchStatus.Empty, null);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _instErrors);
                if (!_authFailed)
                    LogLine($"Leaderboard fetch failed for {song.track.tt} {api}: {ex.Message}");
                return (InstrumentFetchStatus.Error, null);
            }
        }

        private async Task<bool> VerifyTokenAsync(ExchangeCodeToken token)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, "/account/api/oauth/verify");
                req.Headers.Authorization = new AuthenticationHeaderValue(
                    "bearer",
                    token.access_token
                );
                var res = await _httpAccount.SendAsync(req).ConfigureAwait(false);
                var ok = res.IsSuccessStatusCode;
                var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!ok)
                {
                    var (ec, msg) = HttpErrorHelper.ExtractError(body);
                    LogLine(HttpErrorHelper.FormatHttpError("TokenVerify", res, body, ec, msg));
                    return false;
                }
                try
                {
                    using (var doc = JsonDocument.Parse(body))
                    {
                        var root = doc.RootElement;
                        string acc = root.TryGetProperty("account_id", out var aid)
                            ? aid.GetString()
                            : "?";
                        string display = root.TryGetProperty("displayName", out var dn)
                            ? dn.GetString()
                            : "";
                        LogLine($"Verified token for account {acc} {display}");
                    }
                }
                catch
                {
                    LogLine("Token verify parse issue");
                }
                return true;
            }
            catch (Exception ex)
            {
                LogLine("Token verify exception: " + ex.Message);
                return false;
            }
        }

        private async Task<ExchangeCodeToken> GetToken(string exchangeCode)
        {
            try
            {
                var body = new StringContent(
                    $"grant_type=authorization_code&code={exchangeCode}",
                    Encoding.UTF8,
                    "application/x-www-form-urlencoded"
                );
                var req = new HttpRequestMessage(HttpMethod.Post, "/account/api/oauth/token");
                // Correct launcher client basic credential (previous edit had a typo)
                req.Headers.Authorization = new AuthenticationHeaderValue(
                    "basic",
                    "ZWM2ODRiOGM2ODdmNDc5ZmFkZWEzY2IyYWQ4M2Y1YzY6ZTFmMzFjMjExZjI4NDEzMTg2MjYyZDM3YTEzZmM4NGQ="
                );
                req.Content = body;
                var res = await _httpAccount.SendAsync(req).ConfigureAwait(false);
                var str = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    var (ec, msg) = HttpErrorHelper.ExtractError(str);
                    LogLine(HttpErrorHelper.FormatHttpError("TokenRequest", res, str, ec, msg));
                    return null;
                }
                try
                {
                    if (str.TrimStart().StartsWith("{"))
                    {
                        using (var doc = JsonDocument.Parse(str))
                        {
                            var root = doc.RootElement;
                            if (root.TryGetProperty("errorCode", out var ec))
                            {
                                LogLine($"Token error {ec.GetString()}: {root.ToString()}");
                                return null;
                            }
                        }
                    }
                }
                catch { }
                var token = System.Text.Json.JsonSerializer.Deserialize<ExchangeCodeToken>(str);
                if (token == null || string.IsNullOrEmpty(token.access_token))
                    LogLine("Token deserialize failed or empty access_token");
                else
                    LogLine("Received access token (length=" + token.access_token.Length + ")");
                return token;
            }
            catch (Exception ex)
            {
                LogLine("Token request exception: " + ex.Message);
                return null;
            }
        }

        private void LogErrorCodeSummaryIfAny()
        {
            LogLine(HttpErrorHelper.BuildSummaryLine());
        }

        private class ScoreList
        {
            public int score { get; set; }
            public System.Collections.Generic.List<SessionHistory> sessionHistory { get; set; }
        }

        private class SessionHistory
        {
            public TrackedStats trackedStats { get; set; }
        }

        private class TrackedStats
        {
            public int SCORE { get; set; }
            public int ACCURACY { get; set; }
            public int FULL_COMBO { get; set; }
            public int STARS_EARNED { get; set; }
            public int? SEASON { get; set; }
        }
    }
}
