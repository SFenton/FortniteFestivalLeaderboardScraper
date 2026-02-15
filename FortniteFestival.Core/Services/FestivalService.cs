using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
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
    // Logging now limited to error conditions only (performance optimization)
    // Optimized HttpClient instances with increased connection pool limits
#if NETCOREAPP3_0_OR_GREATER || NET5_0_OR_GREATER
        private static readonly SocketsHttpHandler _sharedHandler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 64,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            EnableMultipleHttp2Connections = true,
        };
        private static readonly HttpClient _httpContent = new HttpClient(_sharedHandler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://fortnitecontent-website-prod07.ol.epicgames.com"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        private static readonly HttpClient _httpEvents = new HttpClient(_sharedHandler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://events-public-service-live.ol.epicgames.com"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        private static readonly HttpClient _httpAccount = new HttpClient(_sharedHandler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://account-public-service-prod.ol.epicgames.com"),
            Timeout = TimeSpan.FromSeconds(30),
        };
#else
        // .NET Framework fallback - use default HttpClient (no SocketsHttpHandler available)
        static FestivalService()
        {
            // Increase connection limit for .NET Framework
            ServicePointManager.DefaultConnectionLimit = 64;
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.UseNagleAlgorithm = false;
        }
        private static readonly HttpClient _httpContent = new HttpClient
        {
            BaseAddress = new Uri("https://fortnitecontent-website-prod07.ol.epicgames.com"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        private static readonly HttpClient _httpEvents = new HttpClient
        {
            BaseAddress = new Uri("https://events-public-service-live.ol.epicgames.com"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        private static readonly HttpClient _httpAccount = new HttpClient
        {
            BaseAddress = new Uri("https://account-public-service-prod.ol.epicgames.com"),
            Timeout = TimeSpan.FromSeconds(30),
        };
#endif
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
        
        // Per-song update tracking
        private readonly ConcurrentDictionary<string, bool> _songsCompletedThisPass = new ConcurrentDictionary<string, bool>();
        private readonly ConcurrentDictionary<string, bool> _songsCurrentlyUpdating = new ConcurrentDictionary<string, bool>();
        private readonly ConcurrentQueue<string> _prioritizedSongIds = new ConcurrentQueue<string>();
        
        public bool IsFetching { get; private set; }
        public event Action<string> Log;
        public event Action<string> SongAvailabilityChanged;
        public event Action<LeaderboardData> ScoreUpdated;
        public event Action<int, int, string, bool> SongProgress;
        public event Action<string> SongUpdateStarted;
        public event Action<string> SongUpdateCompleted;
        
        public bool IsSongCompletedThisPass(string songId) => _songsCompletedThisPass.ContainsKey(songId);
        public bool IsSongUpdating(string songId) => _songsCurrentlyUpdating.ContainsKey(songId);
        public bool PrioritizeSong(string songId)
        {
            if (string.IsNullOrEmpty(songId) || !IsFetching) return false;
            // Check if already completed or currently updating
            if (_songsCompletedThisPass.ContainsKey(songId) || _songsCurrentlyUpdating.ContainsKey(songId)) return false;
            _prioritizedSongIds.Enqueue(songId);
            return true;
        }
        
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

        public void SetLogging(bool enabled) { /* no-op */ }

        private void LogLine(string msg)
        {
            try { Log?.Invoke(msg); } catch { }
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
                // removed info log (loaded scores)
                // Dump persisted leaderboard fields for debugging (always, even with logging disabled)
                try
                {
                    Console.WriteLine($"[DBInit] Cached scores loaded: {loadedScores.Count}");
                    void Dump(string inst, ScoreTracker tr, string songId)
                    {
                        if (tr == null) return;
                        Console.WriteLine($"[DBInit] {songId}:{inst} init={tr.initialized} score={tr.maxScore} rank={tr.rank} total={tr.totalEntries}");
                    }
                    foreach (var ld in loadedScores)
                    {
                        Dump("guitar", ld.guitar, ld.songId);
                        Dump("drums", ld.drums, ld.songId);
                        Dump("bass", ld.bass, ld.songId);
                        Dump("vocals", ld.vocals, ld.songId);
                        Dump("pro_guitar", ld.pro_guitar, ld.songId);
                        Dump("pro_bass", ld.pro_bass, ld.songId);
                    }
                    // Proactively raise ScoreUpdated for all loaded boards so any already-open SongInfo pages get percentile immediately
                    foreach (var ld in loadedScores)
                    {
                        try { ScoreUpdated?.Invoke(ld); } catch { }
                    }
                }
                catch { }
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
                    // removed info log (loaded songs)
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
                // removed info log (image root)
            }
            catch (Exception ex)
            {
                LogLine("Image root init failed: " + ex.Message);
            }
            await SyncSongsAsync().ConfigureAwait(false);
            await SyncImagesAsync().ConfigureAwait(false);
            // flush removed
        }

        public async Task SyncSongsAsync()
        {
            // removed info log (sync songs start)
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
                // removed info log (song sync complete)
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
                // flush removed
            }
        }

        private async Task SyncImagesAsync()
        {
            if (_imagesSyncComplete)
                return;
            if (!_songSyncComplete)
            {
                // removed info log (skip image sync)
                return;
            }
                // removed info log (image sync start)
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
                // removed info log (image sync complete)
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

        /// <summary>
        /// Fetch scores using a pre-obtained token (service/headless path).
        /// Skips the exchange-code-to-token step entirely.
        /// </summary>
        public async Task<bool> FetchScoresWithTokenAsync(
            ExchangeCodeToken token,
            IList<string> filteredSongIds,
            Settings settings
        )
        {
            if (IsFetching)
                return false;
            if (!_songSyncComplete || !_imagesSyncComplete)
                return false;
            _authFailed = false;
            _unauthorizedLogged = false;
            IsFetching = true;
            _songsCompletedThisPass.Clear();
            _songsCurrentlyUpdating.Clear();
            while (_prioritizedSongIds.TryDequeue(out _)) { }
            _runSw.Restart();
            _instImproved = 0; _instEmpty = 0; _instErrors = 0; _instRequests = 0; _instBytes = 0;

            if (token == null || string.IsNullOrEmpty(token.access_token))
            {
                LogLine("FetchScoresWithTokenAsync: token is null or has empty access_token.");
                IsFetching = false;
                return false;
            }
            if (!await VerifyTokenAsync(token).ConfigureAwait(false))
            {
                LogLine("FetchScoresWithTokenAsync: token verification failed.");
                IsFetching = false;
                return false;
            }
            return await FetchScoresInternalAsync(token, settings?.DegreeOfParallelism ?? 16, filteredSongIds, settings).ConfigureAwait(false);
        }

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
                return false; // silent early exit
            }
            _authFailed = false;
            _unauthorizedLogged = false;
            IsFetching = true;
            
            // Clear per-pass tracking
            _songsCompletedThisPass.Clear();
            _songsCurrentlyUpdating.Clear();
            while (_prioritizedSongIds.TryDequeue(out _)) { } // clear queue
            
            _runSw.Restart();
            _instImproved = 0;
            _instEmpty = 0;
            _instErrors = 0;
            _instRequests = 0;
            _instBytes = 0;
            // removed info log (authenticating)
            var token = await GetToken(exchangeCode).ConfigureAwait(false);
            if (token == null)
            {
                LogLine("Auth failed (no token). Exchange code may be invalid / already used.");
                IsFetching = false;
                return false;
            }
            // Verify token before heavy work
            if (!await VerifyTokenAsync(token).ConfigureAwait(false))
            {
                LogLine("Token verification failed. Generate a fresh exchange code and retry.");
                IsFetching = false;
                return false;
            }
            return await FetchScoresInternalAsync(token, degreeOfParallelism, filteredSongIds, settings).ConfigureAwait(false);
        }

        private async Task<bool> FetchScoresInternalAsync(
            ExchangeCodeToken token,
            int degreeOfParallelism,
            IList<string> filteredSongIds,
            Settings settings
        )
        {
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
                IsFetching = false;
                return true;
            }
            int fixedDop =
                settings?.DegreeOfParallelism > 0
                    ? settings.DegreeOfParallelism
                    : Math.Max(1, degreeOfParallelism);
                    
            // Build prioritizable queue with remaining songs
            var remainingSongs = new ConcurrentDictionary<string, Song>(
                prioritized.ToDictionary(s => s.track.su, s => s));
            var songQueue = new System.Collections.Concurrent.BlockingCollection<Song>();
            
            // Producer task that feeds the queue, checking for prioritized songs first
            var producerTask = Task.Run(() =>
            {
                var pending = new HashSet<string>(prioritized.Select(s => s.track.su));
                while (pending.Count > 0 && !_authFailed)
                {
                    // Check for prioritized songs first
                    while (_prioritizedSongIds.TryDequeue(out var prioritizedId))
                    {
                        if (pending.Contains(prioritizedId) && remainingSongs.TryRemove(prioritizedId, out var priSong))
                        {
                            pending.Remove(prioritizedId);
                            songQueue.Add(priSong);
                        }
                    }
                    
                    // Get next song from original order
                    var nextSong = prioritized.FirstOrDefault(s => pending.Contains(s.track.su) && remainingSongs.ContainsKey(s.track.su));
                    if (nextSong != null && remainingSongs.TryRemove(nextSong.track.su, out _))
                    {
                        pending.Remove(nextSong.track.su);
                        songQueue.Add(nextSong);
                    }
                    else if (pending.Count > 0)
                    {
                        // Small delay to avoid busy-waiting if queue is being processed
                        Thread.Sleep(1);
                    }
                }
                songQueue.CompleteAdding();
            });
            
            // Track songs processed for incremental persistence
            int songsProcessedSinceLastFlush = 0;
            const int FlushThreshold = 50; // persist every 50 songs
            
            // Consumer tasks
            var semaphore = new SemaphoreSlim(fixedDop, fixedDop);
            var consumerTasks = new List<Task>();
            
            foreach (var song in songQueue.GetConsumingEnumerable())
            {
                if (_authFailed) break;
                await semaphore.WaitAsync().ConfigureAwait(false);
                var currentSong = song;
                var t = Task.Run(async () =>
                {
                    try
                    {
                        // Mark song as currently updating
                        _songsCurrentlyUpdating[currentSong.track.su] = true;
                        try { SongUpdateStarted?.Invoke(currentSong.track.su); } catch { }
                        
                        await FetchSongAsync(currentSong, token, settings).ConfigureAwait(false);
                        
                        // Mark song as completed
                        _songsCurrentlyUpdating.TryRemove(currentSong.track.su, out _);
                        _songsCompletedThisPass[currentSong.track.su] = true;
                        try { SongUpdateCompleted?.Invoke(currentSong.track.su); } catch { }
                        
                        // Incremental persistence: flush dirty scores periodically
                        var processed = Interlocked.Increment(ref songsProcessedSinceLastFlush);
                        if (processed >= FlushThreshold && _persistence != null)
                        {
                            if (Interlocked.CompareExchange(ref songsProcessedSinceLastFlush, 0, processed) == processed)
                            {
                                await FlushDirtyScoresAsync().ConfigureAwait(false);
                            }
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });
                consumerTasks.Add(t);
            }
            
            await producerTask.ConfigureAwait(false);
            await Task.WhenAll(consumerTasks).ConfigureAwait(false);
            if (_authFailed)
                LogLine("Fetch aborted due to authorization failure.");
            if (settings != null)
            {
                settings.DegreeOfParallelism = fixedDop;
            }
            // Final flush of any remaining dirty scores
            if (!_authFailed && _persistence != null)
            {
                await FlushDirtyScoresAsync().ConfigureAwait(false);
            }
            IsFetching = false;
            _runSw.Stop();
            return !_authFailed;
        }

        private void ReportSongFinished(Song s)
        {
            var done = Interlocked.Increment(ref _fetchCompleted);
            // Batch progress updates: only report every 10 songs or at milestones to reduce UI overhead
            bool shouldReport = done == 1 || done == _fetchTotal || done % 10 == 0;
            if (shouldReport)
                SongProgress?.Invoke(done, _fetchTotal, s.track.tt, false);
        }

        private async Task FlushDirtyScoresAsync()
        {
            if (_persistence == null) return;
            try
            {
                var dirty = _scores.Values.Where(s => s.dirty).ToList();
                if (dirty.Count > 0)
                {
                    await _persistence.SaveScoresAsync(dirty).ConfigureAwait(false);
                    foreach (var d in dirty)
                        d.dirty = false;
                }
            }
            catch { }
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
                if (r.board != null)
                    board = r.board; // Capture the board from any result
                if (r.status == InstrumentFetchStatus.Empty)
                {
                    emptyCount++;
                }
                else if (r.status == InstrumentFetchStatus.Error)
                {
                    errorCount++;
                }
            }
            // Always invoke ScoreUpdated so listening UIs can refresh with latest data
            if (board != null && !_authFailed)
                ScoreUpdated?.Invoke(board);
            // Per song summary log
            // removed song summary log
            ReportSongFinished(song);
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
            // V2 removed: Use V1 leaderboard only.
            if (_authFailed)
                return (InstrumentFetchStatus.None, null);
            Interlocked.Increment(ref _instRequests);
            var board = _scores.GetOrAdd(
                song.track.su,
                _ => new LeaderboardData
                {
                    songId = song.track.su,
                    title = song.track.tt,
                    artist = song.track.an,
                }
            );
            if (board.correlatedV1Pages == null)
                board.correlatedV1Pages = new System.Collections.Generic.Dictionary<string, V1LeaderboardPage>(System.StringComparer.OrdinalIgnoreCase);
            var tracker = getter(board) ?? new ScoreTracker();
            var prevScore = tracker.maxScore;
            var prevRank = tracker.rank;
            var prevTotalEntries = tracker.totalEntries;
            // Removed basis-point percentile tracking
            tracker.difficulty = diff;
            // Assumption: Supplying teamAccountIds causes API to return page containing player entry even if not page 0.
            // If this assumption fails (player not on first page), we'll need a follow-up multi-page fetch.
            var page = 0;
            var url = "/api/v1/leaderboards/FNFestival/alltime_" + song.track.su + "_" + api + "/alltime/" + token.account_id + "?page=" + page + "&rank=0&teamAccountIds=" + token.account_id + "&appId=Fortnite&showLiveSessions=false";
            bool debug24k = api == "Solo_Vocals" && string.Equals(song.track.tt, "24K Magic", StringComparison.OrdinalIgnoreCase);
            if (debug24k)
            {
                Console.WriteLine($"[Debug24K] V1 fetch url={url} preRank={tracker.rank} preScore={tracker.maxScore}");
            }
            // General URL trace (without bearer) for troubleshooting
            // removed verbose URL log
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("bearer", token.access_token);
            try
            {
                // removed HTTP trace log
                var res = await _httpEvents.SendAsync(req).ConfigureAwait(false);
                var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!string.IsNullOrEmpty(body))
                    Interlocked.Add(ref _instBytes, body.Length);
                if (!res.IsSuccessStatusCode || string.IsNullOrEmpty(body))
                {
                    if (debug24k)
                        Console.WriteLine($"[Debug24K] V1 status={(int)res.StatusCode} len={body?.Length ?? 0}");
                    Interlocked.Increment(ref _instEmpty);
                    return (InstrumentFetchStatus.Empty, board);
                }
                if (debug24k)
                {
                    Console.WriteLine($"[Debug24K] V1 OK status={(int)res.StatusCode} len={body.Length}");
                    Console.WriteLine($"[Debug24K] V1 RAW={body}");
                }
                int pageVal = 0, totalPagesVal = 0;
                var entries = new System.Collections.Generic.List<V1LeaderboardEntry>();
                try
                {
                    using (var doc = JsonDocument.Parse(body))
                    {
                        var root = doc.RootElement;
                        if (root.TryGetProperty("page", out var pgEl) && pgEl.ValueKind == JsonValueKind.Number)
                            pageVal = pgEl.GetInt32();
                        if (root.TryGetProperty("totalPages", out var tpEl) && tpEl.ValueKind == JsonValueKind.Number)
                            totalPagesVal = tpEl.GetInt32();
                        if (root.TryGetProperty("entries", out var entArr) && entArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var e in entArr.EnumerateArray())
                            {
                                var entry = new V1LeaderboardEntry();
                                // Support both legacy snake_case and current camelCase property names for team id
                                if (e.TryGetProperty("team_id", out var tid) && tid.ValueKind == JsonValueKind.String)
                                    entry.team_id = tid.GetString();
                                else if (e.TryGetProperty("teamId", out var tidc) && tidc.ValueKind == JsonValueKind.String)
                                    entry.team_id = tidc.GetString();
                                if (e.TryGetProperty("rank", out var rk) && rk.ValueKind == JsonValueKind.Number)
                                    entry.rank = rk.GetInt32();
                                if (e.TryGetProperty("pointsEarned", out var pe) && pe.ValueKind == JsonValueKind.Number)
                                    entry.pointsEarned = pe.GetInt32();
                                if (e.TryGetProperty("percentile", out var perc) && (perc.ValueKind == JsonValueKind.Number))
                                {
                                    try { entry.percentile = perc.GetDouble(); } catch { }
                                }
                                if (e.TryGetProperty("sessionHistory", out var sh) && sh.ValueKind == JsonValueKind.Array)
                                {
                                    entry.sessionHistory = new System.Collections.Generic.List<V1SessionHistory>();
                                    foreach (var s in sh.EnumerateArray())
                                    {
                                        var hs = new V1SessionHistory();
                                        if (s.TryGetProperty("trackedStats", out var ts) && ts.ValueKind == JsonValueKind.Object)
                                        {
                                            var stats = new V1TrackedStats();
                                            if (ts.TryGetProperty("SCORE", out var sc) && sc.ValueKind == JsonValueKind.Number) stats.SCORE = sc.GetInt32();
                                            if (ts.TryGetProperty("ACCURACY", out var ac) && ac.ValueKind == JsonValueKind.Number) stats.ACCURACY = ac.GetInt32();
                                            if (ts.TryGetProperty("FULL_COMBO", out var fc) && fc.ValueKind == JsonValueKind.Number) stats.FULL_COMBO = fc.GetInt32();
                                            if (ts.TryGetProperty("STARS_EARNED", out var se) && se.ValueKind == JsonValueKind.Number) stats.STARS_EARNED = se.GetInt32();
                                            if (ts.TryGetProperty("SEASON", out var sea) && sea.ValueKind == JsonValueKind.Number) stats.SEASON = sea.GetInt32();
                                            hs.trackedStats = stats;
                                            if (stats.SCORE > entry.score) entry.score = stats.SCORE; // derive best score
                                        }
                                        entry.sessionHistory.Add(hs);
                                    }
                                }
                                entries.Add(entry);
                            }
                        }
                    }
                }
                catch (Exception pex)
                {
                    LogLine("V1 parse error: " + pex.Message);
                    Interlocked.Increment(ref _instErrors);
                    return (InstrumentFetchStatus.Error, board);
                }
                var pageObj = new V1LeaderboardPage { page = pageVal, totalPages = totalPagesVal, entries = entries };
                board.correlatedV1Pages[api] = pageObj;
                // Determine highest score among entries with a valid percentile (not -1) for diagnostics / potential heuristics
                int highestValidScore = 0;
                if (entries.Count > 0)
                {
                    foreach (var ev in entries)
                    {
                        if (ev.percentile != -1 && ev.score > highestValidScore)
                            highestValidScore = ev.score;
                    }
                }
                // removed page stats log
                // Update tracker from player's entry
                var playerEntry = entries.FirstOrDefault(e => string.Equals(e.team_id, token.account_id, StringComparison.OrdinalIgnoreCase));
                // removed improvement trace log
                if (playerEntry == null && entries.Count > 0)
                {
                    // Emit a small sample of team ids for diagnostics (avoid spamming full list)
                    var sampleIds = string.Join(",", entries.Take(5).Select(e => e.team_id));
                    // removed player entry not found diagnostic
                }
                if (playerEntry != null)
                {
                    // Populate score + stats based on best session for that entry
                    int bestScore = playerEntry.score;
                    V1TrackedStats bestStats = null;
                    if (playerEntry.sessionHistory != null)
                    {
                        foreach (var h in playerEntry.sessionHistory)
                        {
                            if (h?.trackedStats == null) continue;
                            if (h.trackedStats.SCORE == bestScore)
                                bestStats = h.trackedStats;
                        }
                    }
                    tracker.rank = playerEntry.rank;
                    bool scoreImproved = bestScore > tracker.maxScore;
                    if (scoreImproved)
                        tracker.maxScore = bestScore;
                    tracker.initialized = tracker.maxScore > 0 || tracker.rank > 0;
                    if (bestStats != null)
                    {
                        tracker.percentHit = bestStats.ACCURACY;
                        tracker.isFullCombo = bestStats.FULL_COMBO == 1;
                        tracker.numStars = bestStats.STARS_EARNED;
                        // Always update season from API when available - the API reflects the current high score's season
                        if (bestStats.SEASON > 0)
                            tracker.seasonAchieved = bestStats.SEASON;
                    }
                    // Persist API-provided raw percentile if present (>0)
                    if (playerEntry.percentile > 0)
                        tracker.rawPercentile = playerEntry.percentile;
                    // Reverse-calc total entries: assume rawPercentile ≈ rank / total.
                    if (tracker.rank > 0 && tracker.rawPercentile > 1e-9)
                    {
                        var estimate = (int)Math.Round(tracker.rank / tracker.rawPercentile);
                        if (estimate < tracker.rank) estimate = tracker.rank; // floor at rank
                        if (estimate > 10_000_000) estimate = 10_000_000; // cap sanity
                        tracker.calculatedNumEntries = estimate;
                    }
                }
                // Removed legacy totalEntries approximation (totalPages*100). We now rely on actual data (future enhancement: fetch exact page containing player to set rank/total accurately).
                // If we already have totalEntries (from earlier precise logic) and rank, recompute bp; otherwise leave as-is.
                // Percentile basis points removed
                tracker.RefreshDerived();
                bool scoreIncrease = tracker.maxScore > prevScore;
                bool newRankAppeared = (prevRank == 0 && tracker.rank > 0);
                bool improved = scoreIncrease || newRankAppeared;
                bool metaChanged = !improved && (tracker.rank != prevRank || tracker.totalEntries != prevTotalEntries);
                // removed improvement eval log
                assign(board, tracker);
                if (improved)
                {
                    board.dirty = true;
                    Interlocked.Increment(ref _instImproved);
                    // removed improvement log
                    try { ScoreUpdated?.Invoke(board); } catch { }
                    return (InstrumentFetchStatus.Improved, board);
                }
                if (metaChanged)
                {
                    // removed improvement meta-change log
                    board.dirty = true; // ensure new rank/percentile persisted
                    try { ScoreUpdated?.Invoke(board); } catch { }
                }
                Interlocked.Increment(ref _instEmpty);
                // removed no-improvement log
                return (InstrumentFetchStatus.Empty, board);
            }
            catch (Exception ex)
            {
                if (debug24k)
                    Console.WriteLine("[Debug24K] V1 exception: " + ex.Message);
                Interlocked.Increment(ref _instErrors);
                LogLine($"V1 fetch failed for {song.track.tt} {api}: {ex.Message}");
                return (InstrumentFetchStatus.Error, board);
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
                        // removed token verified log
                    }
                }
                catch
                {
                    // removed token verify parse issue (non-critical)
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
                {
                    LogLine("Token deserialize failed or empty access_token");
                    return null;
                }
                else
                {
                    // removed received token length log
                    return token;
                }
            }
            catch (Exception ex)
            {
                LogLine("Token request exception: " + ex.Message);
                return null;
            }
        }

        private void LogErrorCodeSummaryIfAny()
        {
            // removed summary log
        }

    // Removed old V2 helper and separate correlation method; V1 fetching now integrated in FetchInstrumentAsync.
    }
}
