using FortniteFestivalLeaderboardScraper.Helpers.Leaderboard;
using Newtonsoft.Json;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Threading;

namespace FortniteFestivalLeaderboardScraper.Helpers
{
    public class LeaderboardAPI
    {
        private const string API_BASE_ADDRESS = "https://events-public-service-live.ol.epicgames.com/api/v2/games/FNFestival/leaderboards/";
        // New base for v1 all-time endpoints (page based) – still available for diagnostics
        private const string API_V1_ALLTIME_BASE = "https://events-public-service-live.ol.epicgames.com/api/v1/leaderboards/FNFestival/";

        private const string DRUMS = "Solo_Drums";
        private const string VOCALS = "Solo_Vocals";
        private const string BASS = "Solo_Bass";
        private const string GUITAR = "Solo_Guitar";
        private const string PRO_BASS = "Solo_PeripheralBass";
        private const string PRO_GUITAR = "Solo_PeripheralGuitar";

        private string PARAMS = "fromIndex=0&findTeams=false";
        private string GetFriendlyInstrumentName(string instrumentName)
        {
            switch (instrumentName)
            {
                case DRUMS: return "Drums";
                case VOCALS: return "Vocals";
                case BASS: return "Bass";
                case GUITAR: return "Guitar";
                case PRO_BASS: return "Pro Bass";
                case PRO_GUITAR: return "Pro Guitar";
            }
            return "Unknown Instrument";
        }

        private struct InstrumentInfo
        {
            public string ApiName; // e.g. Solo_Guitar
            public Func<Song, int> DifficultySelector; // pick difficulty from manifest
            public Action<LeaderboardData, ScoreTracker> Assign; // assign to LeaderboardData
            public Func<LeaderboardData, ScoreTracker> Getter; // get existing tracker
            public string Friendly;
        }

        private InstrumentInfo[] GetInstrumentInfos()
        {
            return new InstrumentInfo[]
            {
                new InstrumentInfo { ApiName = DRUMS, DifficultySelector = s => s.track.@in.ds, Assign = (ld, st)=> ld.drums = st, Getter = ld=> ld.drums, Friendly = "Drums" },
                new InstrumentInfo { ApiName = GUITAR, DifficultySelector = s => s.track.@in.gr, Assign = (ld, st)=> ld.guitar = st, Getter = ld=> ld.guitar, Friendly = "Guitar" },
                new InstrumentInfo { ApiName = PRO_BASS, DifficultySelector = s => s.track.@in.pb, Assign = (ld, st)=> ld.pro_bass = st, Getter = ld=> ld.pro_bass, Friendly = "Pro Bass" },
                new InstrumentInfo { ApiName = PRO_GUITAR, DifficultySelector = s => s.track.@in.pg, Assign = (ld, st)=> ld.pro_guitar = st, Getter = ld=> ld.pro_guitar, Friendly = "Pro Guitar" },
                new InstrumentInfo { ApiName = BASS, DifficultySelector = s => s.track.@in.ba, Assign = (ld, st)=> ld.bass = st, Getter = ld=> ld.bass, Friendly = "Bass" },
                new InstrumentInfo { ApiName = VOCALS, DifficultySelector = s => s.track.@in.vl, Assign = (ld, st)=> ld.vocals = st, Getter = ld=> ld.vocals, Friendly = "Vocals" }
            };
        }

        // v1 GET (paged) still here if needed for debug
        public async Task<AllTimeLeaderboardPage> GetAllTimeLeaderboardPageAsync(string eventId, string allTimeWindowId, string instrumentPath, string accessToken, string accountId, int page = 0, int rank = 0)
        {
            var url = $"{API_V1_ALLTIME_BASE}{eventId}/{allTimeWindowId}/{accountId}?page={page}&rank={rank}&teamAccountIds={accountId}&appId=Fortnite&showLiveSessions=false";
            var client = new RestClient(url);
            var request = new RestRequest();
            request.Method = Method.Get;
            request.AddHeader("Authorization", "Bearer " + accessToken);
            request.AddHeader("Accept-Encoding", "deflate, gzip");
            request.AddHeader("Accept", "application/json");
            var response = await client.ExecuteAsync(request);
            try { return JsonConvert.DeserializeObject<AllTimeLeaderboardPage>(response.Content); } catch { return null; }
        }

        public async Task<List<AllTimeLeaderboardPage>> GetAllTimeLeaderboardSampleAsync(string eventId, string allTimeWindowId, string instrumentPath, string accessToken, string accountId, int pagesToFetch = 1)
        {
            var results = new List<AllTimeLeaderboardPage>();
            for (int p = 0; p < pagesToFetch; p++)
            {
                var page = await GetAllTimeLeaderboardPageAsync(eventId, allTimeWindowId, instrumentPath, accessToken, accountId, p, 0);
                if (page == null) break;
                results.Add(page);
                if (page.page >= page.totalPages - 1) break;
            }
            return results;
        }

        // Existing sequential method retained for fallback
        public async Task<Tuple<bool, List<LeaderboardData>>> GetLeaderboardsForInstrument(
            List<Song> items,
            string accessToken,
            string accountId,
            int unusedMaxSeason, // kept parameter for signature compatibility
            List<LeaderboardData> prevData,
            System.Windows.Forms.TextBox textBox,
            List<string> filteredSongIds)
        {
            if (items.Count == 0)
                return new Tuple<bool, List<LeaderboardData>>(true, new List<LeaderboardData>());

            var leaderboardData = new List<LeaderboardData>();

            foreach (Song song in items)
            {
                if (filteredSongIds.Count > 0 && !filteredSongIds.Contains(song.track.su))
                {
                    var existing = prevData.Find(x => x.songId == song.track.su);
                    if (existing != null) leaderboardData.Add(existing);
                    continue;
                }

                var songBoard = new LeaderboardData
                {
                    title = song.track.tt,
                    artist = song.track.an,
                    songId = song.track.su
                };

                // Loop instruments (fixed order for mapping)
                for (int i = 0; i < 6; i++)
                {
                    string instrumentName;
                    int difficulty = 0;
                    switch (i)
                    {
                        case 0: instrumentName = DRUMS; difficulty = song.track.@in.ds; break;
                        case 1: instrumentName = GUITAR; difficulty = song.track.@in.gr; break;
                        case 2: instrumentName = PRO_BASS; difficulty = song.track.@in.pb; break;
                        case 3: instrumentName = PRO_GUITAR; difficulty = song.track.@in.pg; break;
                        case 4: instrumentName = BASS; difficulty = song.track.@in.ba; break;
                        case 5: instrumentName = VOCALS; difficulty = song.track.@in.vl; break;
                        default: instrumentName = DRUMS; break;
                    }

                    var instrumentData = new ScoreTracker();
                    var prevSongData = prevData.Find(x => x.songId == song.track.su);
                    if (prevSongData != null)
                    {
                        switch (i)
                        {
                            case 0: instrumentData = prevSongData.drums ?? new ScoreTracker(); break;
                            case 1: instrumentData = prevSongData.guitar ?? new ScoreTracker(); break;
                            case 2: instrumentData = prevSongData.pro_bass ?? new ScoreTracker(); break;
                            case 3: instrumentData = prevSongData.pro_guitar ?? new ScoreTracker(); break;
                            case 4: instrumentData = prevSongData.bass ?? new ScoreTracker(); break;
                            case 5: instrumentData = prevSongData.vocals ?? new ScoreTracker(); break;
                        }
                    }

                    // Ensure difficulty is kept up to date from manifest
                    instrumentData.difficulty = difficulty;

                    // Build all-time v2 URL
                    var url = API_BASE_ADDRESS + $"alltime_{song.track.su}_{instrumentName}/alltime/scores?accountId={accountId}&{PARAMS}";
                    var client = new RestClient(url);
                    var request = new RestRequest();
                    request.Method = Method.Post;
                    request.AddHeader("Authorization", "bearer " + accessToken);
                    request.AddHeader("Accept-Encoding", "gzip, deflate, br");
                    request.AddHeader("Content-Type", "application/json");
                    request.AddHeader("Accept", "application/json");
                    request.AddParameter("", "{\"teams\":[[\"" + accountId + "\"]]}", ParameterType.RequestBody);

                    textBox.AppendText(Environment.NewLine + "Getting ALL-TIME leaderboard for " + song.track.tt + " (" + GetFriendlyInstrumentName(instrumentName) + ")");

                    var res = await client.ExecuteAsync(request);

                    try
                    {
                        var leaderboard = JsonConvert.DeserializeObject<ScoreList[]>(res.Content);
                        if (leaderboard != null)
                        {
                            foreach (var score in leaderboard)
                            {
                                if (score.score > instrumentData.maxScore)
                                {
                                    instrumentData.maxScore = score.score;
                                    instrumentData.initialized = true;
                                    foreach (var session in score.sessionHistory)
                                    {
                                        if (session.trackedStats.SCORE == score.score)
                                        {
                                            instrumentData.percentHit = session.trackedStats.ACCURACY;
                                            instrumentData.isFullCombo = session.trackedStats.FULL_COMBO == 1;
                                            instrumentData.numStars = session.trackedStats.STARS_EARNED;
                                            instrumentData.seasonAchieved = session.trackedStats.SEASON;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                        var error = JsonConvert.DeserializeObject<InvalidSeason>(res.Content);
                        if (error != null && error.errorCode == "com.epicgames.events.unauthorized")
                        {
                            textBox.AppendText(Environment.NewLine + "Access token unauthorized during all-time fetch.");
                            return new Tuple<bool, List<LeaderboardData>>(false, new List<LeaderboardData>());
                        }
                        // For all-time, treat missing/no score as simply no update.
                    }

                    switch (i)
                    {
                        case 0: songBoard.drums = instrumentData; break;
                        case 1: songBoard.guitar = instrumentData; break;
                        case 2: songBoard.pro_bass = instrumentData; break;
                        case 3: songBoard.pro_guitar = instrumentData; break;
                        case 4: songBoard.bass = instrumentData; break;
                        case 5: songBoard.vocals = instrumentData; break;
                    }
                }

                leaderboardData.Add(songBoard);
            }

            return new Tuple<bool, List<LeaderboardData>>(true, leaderboardData);
        }

        // NEW: Parallelized all-time retrieval across songs & instruments
        public async Task<Tuple<bool, List<LeaderboardData>>> GetLeaderboardsParallel(
            List<Song> songs,
            string accessToken,
            string accountId,
            int unusedMaxSeason,
            List<LeaderboardData> previousData,
            System.Windows.Forms.TextBox textBox,
            List<string> filteredSongIds,
            int degreeOfParallelism = 12,
            Action<LeaderboardData> perSongCompleted = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (songs == null || songs.Count == 0)
                return new Tuple<bool, List<LeaderboardData>>(true, new List<LeaderboardData>());

            // Filter upfront if user selected subset
            var targetSongs = (filteredSongIds != null && filteredSongIds.Count > 0)
                ? songs.FindAll(s => filteredSongIds.Contains(s.track.su))
                : songs;

            var instrumentInfos = GetInstrumentInfos();
            var dataMap = new ConcurrentDictionary<string, LeaderboardData>();
            var songLocks = new ConcurrentDictionary<string, object>();
            var instrumentCompletionCounts = new ConcurrentDictionary<string, int>();
            var unauthorized = false;

            Action<string> log = (msg) =>
            {
                try
                {
                    if (textBox != null)
                    {
                        if (textBox.InvokeRequired)
                        {
                            textBox.BeginInvoke(new Action(() => textBox.AppendText(Environment.NewLine + msg)));
                        }
                        else
                        {
                            textBox.AppendText(Environment.NewLine + msg);
                        }
                    }
                }
                catch { /* ignore UI thread issues */ }
            };

            // Pre-create data objects copying any existing values
            foreach (var song in targetSongs)
            {
                var existing = previousData.Find(x => x.songId == song.track.su);
                var board = existing != null ? existing : new LeaderboardData
                {
                    songId = song.track.su,
                    title = song.track.tt,
                    artist = song.track.an
                };

                // Always refresh title/artist in case of manifest change
                board.title = song.track.tt;
                board.artist = song.track.an;

                // Ensure ScoreTrackers exist & update difficulty from manifest
                foreach (var info in instrumentInfos)
                {
                    var tracker = info.Getter(board) ?? new ScoreTracker();
                    tracker.difficulty = info.DifficultySelector(song);
                    info.Assign(board, tracker);
                }

                dataMap[song.track.su] = board;
                songLocks[song.track.su] = new object();
                instrumentCompletionCounts[song.track.su] = 0;
            }

            var semaphore = new SemaphoreSlim(degreeOfParallelism);
            var tasks = new List<Task>();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            foreach (var song in targetSongs)
            {
                foreach (var info in instrumentInfos)
                {
                    var localSong = song; // capture
                    var localInfo = info;
                    tasks.Add(Task.Run(async () =>
                    {
                        await semaphore.WaitAsync(cts.Token).ConfigureAwait(false);
                        try
                        {
                            if (cts.IsCancellationRequested) return;

                            log($"Getting ALL-TIME leaderboard for {localSong.track.tt} ({localInfo.Friendly})");

                            var url = API_BASE_ADDRESS + $"alltime_{localSong.track.su}_{localInfo.ApiName}/alltime/scores?accountId={accountId}&{PARAMS}";
                            var client = new RestClient(url);
                            var request = new RestRequest();
                            request.Method = Method.Post;
                            request.AddHeader("Authorization", "bearer " + accessToken);
                            request.AddHeader("Accept-Encoding", "gzip, deflate, br");
                            request.AddHeader("Content-Type", "application/json");
                            request.AddHeader("Accept", "application/json");
                            request.AddParameter("", "{\"teams\":[[\"" + accountId + "\"]]}", ParameterType.RequestBody);

                            var res = await client.ExecuteAsync(request);

                            ScoreTracker updatedTracker = null;
                            try
                            {
                                var leaderboard = JsonConvert.DeserializeObject<ScoreList[]>(res.Content);
                                if (leaderboard != null)
                                {
                                    // get current tracker
                                    updatedTracker = localInfo.Getter(dataMap[localSong.track.su]) ?? new ScoreTracker();
                                    foreach (var score in leaderboard)
                                    {
                                        if (score.score > updatedTracker.maxScore)
                                        {
                                            updatedTracker.maxScore = score.score;
                                            updatedTracker.initialized = true;
                                            if (score.sessionHistory != null)
                                            {
                                                foreach (var session in score.sessionHistory)
                                                {
                                                    if (session.trackedStats != null && session.trackedStats.SCORE == score.score)
                                                    {
                                                        updatedTracker.percentHit = session.trackedStats.ACCURACY;
                                                        updatedTracker.isFullCombo = session.trackedStats.FULL_COMBO == 1;
                                                        updatedTracker.numStars = session.trackedStats.STARS_EARNED;
                                                        updatedTracker.seasonAchieved = session.trackedStats.SEASON;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception)
                            {
                                var error = JsonConvert.DeserializeObject<InvalidSeason>(res.Content);
                                if (error != null && error.errorCode == "com.epicgames.events.unauthorized")
                                {
                                    unauthorized = true;
                                    log("Access token unauthorized during all-time fetch (parallel)");
                                    cts.Cancel();
                                }
                            }

                            if (updatedTracker != null)
                            {
                                lock (songLocks[localSong.track.su])
                                {
                                    // ensure difficulty kept (in rare case no score update)
                                    updatedTracker.difficulty = localInfo.DifficultySelector(localSong);
                                    localInfo.Assign(dataMap[localSong.track.su], updatedTracker);
                                }
                            }

                            // Instrument completion count; if all instruments done -> callback
                            // Interlocked.Increment cannot be used directly on dictionary indexer in older C#; use AddOrUpdate-like loop
                            int newVal = instrumentCompletionCounts.AddOrUpdate(localSong.track.su, 1, (k, v) => v + 1);
                            if (newVal == instrumentInfos.Length)
                            {
                                if (perSongCompleted != null)
                                {
                                    try { perSongCompleted(dataMap[localSong.track.su]); } catch { }
                                }
                            }
                        }
                        catch (OperationCanceledException) { }
                        finally
                        {
                            semaphore.Release();
                        }
                    }, cts.Token));
                }
            }

            try
            {
                await Task.WhenAll(tasks.ToArray());
            }
            catch (OperationCanceledException) { }

            if (unauthorized)
            {
                return new Tuple<bool, List<LeaderboardData>>(false, new List<LeaderboardData>());
            }

            var finalList = new List<LeaderboardData>(dataMap.Values);
            finalList.Sort((a, b) => string.Compare(a.artist, b.artist, StringComparison.OrdinalIgnoreCase));
            return new Tuple<bool, List<LeaderboardData>>(true, finalList);
        }
    }
}