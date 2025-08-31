using FortniteFestivalLeaderboardScraper.Helpers.Leaderboard;
using Newtonsoft.Json;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

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

        // New refactored method: all-time v2 only (replaces season-by-season)
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
                    // Pattern: alltime_{songId}_{Instrument}/alltime/scores
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
    }
}