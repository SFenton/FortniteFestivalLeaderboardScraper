using FortniteFestival.Core.Services;
using FortniteFestival.Core.Net;
using FortniteFestival.Core.Auth;
using FortniteFestival.Core.Persistence;
using Newtonsoft.Json;
using RestSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace FortniteFestival.Core.Services
{
    public class FestivalService : IFestivalService
    {
        private readonly object _sync = new object();
        private readonly ConcurrentDictionary<string, LeaderboardData> _scores = new ConcurrentDictionary<string, LeaderboardData>();
        private readonly Dictionary<string, Song> _songs = new Dictionary<string, Song>();
        private bool _initialized;
        private bool _songSyncComplete;
        private volatile bool _authFailed;
        public bool IsFetching { get; private set; }
        public event Action<string> Log; public event Action<string> SongAvailabilityChanged; public event Action<LeaderboardData> ScoreUpdated; public event Action<int,int,string,bool> SongProgress;
        public IReadOnlyList<Song> Songs => _songs.Values.ToList();
        public IReadOnlyDictionary<string, LeaderboardData> ScoresIndex => _scores;
        private void LogLine(string msg) => Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {msg}");
        private readonly IFestivalPersistence _persistence;
        public FestivalService() : this(null) {}
        public FestivalService(IFestivalPersistence persistence){ _persistence = persistence; }
        public async Task InitializeAsync(){ if (_initialized) return; _initialized = true; if(_persistence!=null){ var loadedScores = await _persistence.LoadScoresAsync(); foreach(var ld in loadedScores) _scores[ld.songId]=ld; LogLine($"Loaded {loadedScores.Count} cached scores."); var loadedSongs = await _persistence.LoadSongsAsync(); if(loadedSongs!=null && loadedSongs.Count>0){ foreach(var s in loadedSongs){ if(!_songs.ContainsKey(s.track.su)) _songs[s.track.su]=s; } LogLine($"Loaded {loadedSongs.Count} cached songs."); } } await SyncSongsAsync(); }
        public async Task SyncSongsAsync()
        {
            LogLine("Syncing songs (unauthenticated)...");
            try
            {
                var req = new RestRequest("/content/api/pages/fortnite-game/spark-tracks", Method.Get);
                var res = await RestClients.Content.ExecuteAsync(req);
                var token = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JToken>(res.Content);
                var list = new List<Song>();
                foreach (var child in token.Children())
                {
                    try
                    {
                        var json = child.ToString();
                        var obj = JsonConvert.DeserializeObject<Song>(json.Substring(json.IndexOf('{')));
                        list.Add(obj);
                    }
                    catch { }
                }
                lock (_sync)
                {
                    var incomingIds = new HashSet<string>(list.Select(s => s.track.su));
                    var stale = _songs.Keys.Where(k => !incomingIds.Contains(k)).ToList();
                    foreach (var id in stale) _songs.Remove(id);
                    foreach (var s in list)
                    {
                        if (_songs.TryGetValue(s.track.su, out var existing))
                        {
                            existing.track = s.track; existing._activeDate = s._activeDate; existing.lastModified = s.lastModified;
                        }
                        else _songs[s.track.su] = s;
                    }
                }
                LogLine($"Song sync complete. {_songs.Count} songs loaded.");
                if (_persistence != null)
                {
                    try { await _persistence.SaveSongsAsync(_songs.Values); } catch { }
                }
            }
            catch (Exception ex)
            {
                LogLine("Song sync failed: " + ex.Message);
            }
            finally { _songSyncComplete = true; }
        }
        public async Task<bool> FetchScoresAsync(string exchangeCode, int degreeOfParallelism, IList<string> filteredSongIds, FortniteFestival.Core.Config.Settings settings){ if(IsFetching) return false; if(!_songSyncComplete){ LogLine("Songs not yet synced."); return false; } _authFailed=false; IsFetching = true; LogLine("Authenticating..."); var token = await GetToken(exchangeCode); if(token==null){ LogLine("Auth failed."); IsFetching=false; return false; }
            var prioritized = Songs.Select((s,i)=>new{ s,i}).OrderBy(x=> _scores.ContainsKey(x.s.track.su)?1:0).ThenBy(x=>x.i).Select(x=>x.s).ToList();
            if(filteredSongIds!=null && filteredSongIds.Count>0) prioritized = prioritized.Where(s=> filteredSongIds.Contains(s.track.su)).ToList();
            int total = prioritized.Count; if(total==0){ LogLine("No songs selected."); IsFetching=false; return true; }
            LogLine($"Fetching leaderboards for {total} songs with adaptive concurrency...");
            var queue = new Queue<Song>(prioritized);
            var active = new List<Task<long>>();
            int indexCounter = 0; var swPerSong = new Queue<long>(); const int Window=8; // sliding window
            int dynamicDop = settings?.DegreeOfParallelism>0 ? settings.DegreeOfParallelism : Math.Max(1, degreeOfParallelism);
            int maxDop = 128; int minDop = 1;
            var globalSw = Stopwatch.StartNew();
            while((queue.Count>0 || active.Count>0) && !_authFailed)
            {
                // launch up to dynamicDop
                while(active.Count < dynamicDop && queue.Count>0 && !_authFailed)
                {
                    var song = queue.Dequeue(); int songIndex = ++indexCounter; SongProgress?.Invoke(songIndex,total,song.track.tt,true); LogLine($"Starting {songIndex}/{total}: {song.track.tt}"); var swSong = Stopwatch.StartNew();
                    var t = Task.Run(async ()=>{ try { await FetchSongAsync(song, token, settings, songIndex, total); } finally { swSong.Stop(); } return swSong.ElapsedMilliseconds; });
                    active.Add(t);
                }
                if(active.Count==0) break;
                var finished = await Task.WhenAny(active); active.Remove(finished);
                long elapsed = 0; try { elapsed = finished.Result; } catch { }
                if(elapsed>0){ swPerSong.Enqueue(elapsed); while(swPerSong.Count>Window) swPerSong.Dequeue(); }
                // Adapt only if we have a full window or end of queue
                if(swPerSong.Count==Window || (queue.Count==0 && active.Count==0))
                {
                    double avgMs = swPerSong.Average();
                    // Heuristic: target per-song latency sweet spot around 1500-3000ms; if faster, we can add more concurrency; if much slower, reduce.
                    int old = dynamicDop;
                    if(avgMs < 1500 && dynamicDop < maxDop) dynamicDop = Math.Min(maxDop, dynamicDop + Math.Max(1, dynamicDop/8));
                    else if(avgMs > 4500 && dynamicDop > minDop) dynamicDop = Math.Max(minDop, dynamicDop - Math.Max(1, dynamicDop/10));
                    if(dynamicDop!=old) LogLine($"Adaptive concurrency change: {old} -> {dynamicDop} (avg {avgMs:0} ms)");
                }
            }
            globalSw.Stop();
            if(!_authFailed) LogLine($"Score fetch complete in {globalSw.Elapsed.TotalSeconds:0.0}s. Final concurrency baseline {dynamicDop}."); else LogLine("Fetch aborted due to authorization failure.");
            if(settings!=null){ settings.DegreeOfParallelism = dynamicDop; }
            if(!_authFailed && _persistence!=null){ try { await _persistence.SaveScoresAsync(_scores.Values); LogLine("Scores persisted."); } catch { } }
            IsFetching=false; return !_authFailed; }
        private async Task FetchSongAsync(Song song, ExchangeCodeToken token, FortniteFestival.Core.Config.Settings settings, int songIndex, int total)
        {
            var instruments = new List<(string api, Func<LeaderboardData, ScoreTracker> getter, Action<LeaderboardData, ScoreTracker> assign, int diff)>();
            if(settings==null || settings.QueryDrums) instruments.Add(("Solo_Drums", l=>l.drums, (l,s)=> l.drums=s, song.track.@in.ds));
            if(settings==null || settings.QueryLead) instruments.Add(("Solo_Guitar", l=>l.guitar, (l,s)=> l.guitar=s, song.track.@in.gr));
            if(settings==null || settings.QueryProBass) instruments.Add(("Solo_PeripheralBass", l=>l.pro_bass, (l,s)=> l.pro_bass=s, song.track.@in.pb));
            if(settings==null || settings.QueryProLead) instruments.Add(("Solo_PeripheralGuitar", l=>l.pro_guitar, (l,s)=> l.pro_guitar=s, song.track.@in.pg));
            if(settings==null || settings.QueryBass) instruments.Add(("Solo_Bass", l=>l.bass, (l,s)=> l.bass=s, song.track.@in.ba));
            if(settings==null || settings.QueryVocals) instruments.Add(("Solo_Vocals", l=>l.vocals, (l,s)=> l.vocals=s, song.track.@in.vl));
            bool anySuccess = false; LeaderboardData board=null;
            foreach(var tup in instruments)
            { if(_authFailed) break; var api = tup.api; var getter = tup.getter; var assign = tup.assign; var diff = tup.diff; var req = new RestRequest($"/api/v2/games/FNFestival/leaderboards/alltime_{song.track.su}_{api}/alltime/scores?accountId={token.account_id}&fromIndex=0&findTeams=false", Method.Post); req.AddHeader("Authorization","bearer "+token.access_token); req.AddHeader("Content-Type","application/json"); req.AddParameter("","{\"teams\":[[\""+token.account_id+"\"]]}", ParameterType.RequestBody); try { var res = await RestClients.Events.ExecuteAsync(req); if(string.IsNullOrEmpty(res.Content)) throw new Exception("Empty response"); ScoreList[] arr=null; if(res.Content.TrimStart().StartsWith("{")){ var obj = Newtonsoft.Json.Linq.JObject.Parse(res.Content); if(obj["errorCode"]!=null){ var ec=(string)obj["errorCode"]; if(ec.IndexOf("unauthorized",StringComparison.OrdinalIgnoreCase)>=0){ _authFailed=true; LogLine(ec); break;} else if(ec.IndexOf("not_found",StringComparison.OrdinalIgnoreCase)>=0){ arr=new ScoreList[0]; } else throw new Exception(ec);} else { arr=new ScoreList[0]; } } else { arr=JsonConvert.DeserializeObject<ScoreList[]>(res.Content); } if(_authFailed) break; if(arr!=null && arr.Length>0){ if(board==null) board=_scores.GetOrAdd(song.track.su,_=> new LeaderboardData{ songId=song.track.su,title=song.track.tt,artist=song.track.an}); var tracker = getter(board)?? new ScoreTracker(); tracker.difficulty=diff; foreach(var sc in arr){ if(sc.score>tracker.maxScore){ tracker.maxScore=sc.score; tracker.initialized=true; if(sc.sessionHistory!=null){ foreach(var session in sc.sessionHistory){ if(session.trackedStats!=null && session.trackedStats.SCORE==sc.score){ tracker.percentHit=session.trackedStats.ACCURACY; tracker.isFullCombo=session.trackedStats.FULL_COMBO==1; tracker.numStars=session.trackedStats.STARS_EARNED; tracker.seasonAchieved=session.trackedStats.SEASON??0; } } } } } assign(board, tracker); anySuccess = anySuccess || tracker.initialized; } } catch(Exception ex){ if(!_authFailed) LogLine($"Leaderboard fetch failed for {song.track.tt} {api}: {ex.Message}"); } }
            if(anySuccess && !_authFailed) ScoreUpdated?.Invoke(board); if(!_authFailed){ LogLine($"Finished {songIndex}/{total}: {song.track.tt}"); SongProgress?.Invoke(songIndex,total,song.track.tt,false);} }
        private async Task<ExchangeCodeToken> GetToken(string exchangeCode){ try { var req = new RestRequest("/account/api/oauth/token", Method.Post); req.AddHeader("Authorization","basic ZWM2ODRiOGM2ODdmNDc5ZmFkZWEzY2IyYWQ4M2Y1YzY6ZTFmMzFjMjExZjI4NDEzMTg2MjYyZDM3YTEzZmM4NGQ="); req.AddHeader("Content-Type","application/x-www-form-urlencoded"); req.AddParameter("application/x-www-form-urlencoded","grant_type=authorization_code&code="+exchangeCode, ParameterType.RequestBody); var res = await RestClients.Account.ExecuteAsync(req); return JsonConvert.DeserializeObject<ExchangeCodeToken>(res.Content); } catch { return null; } }
        private class ScoreList{ public int score { get; set; } public List<SessionHistory> sessionHistory { get; set; } }
        private class SessionHistory{ public TrackedStats trackedStats { get; set; } }
        private class TrackedStats{ public int SCORE { get; set; } public int ACCURACY { get; set; } public int FULL_COMBO { get; set; } public int STARS_EARNED { get; set; } public int? SEASON { get; set; } }
    }
}
