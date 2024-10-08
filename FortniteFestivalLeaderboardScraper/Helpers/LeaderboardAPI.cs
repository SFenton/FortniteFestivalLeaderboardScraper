﻿using FortniteFestivalLeaderboardScraper.Helpers.Leaderboard;
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

        private const string DRUMS = "Solo_Drums";
        private const string VOCALS = "Solo_Vocals";
        private const string BASS = "Solo_Bass";
        private const string GUITAR = "Solo_Guitar";
        private const string PRO_BASS = "Solo_PeripheralBass";
        private const string PRO_GUITAR = "Solo_PeripheralGuitar";
        private const int PRO_STRINGS_MINSEASON = 3;

        private string PARAMS = "fromIndex=0&findTeams=false";
        private string GetFriendlyInstrumentName(string instrumentName)
        {
            switch (instrumentName)
            {
                case DRUMS:
                    return "Drums";
                case VOCALS:
                    return "Vocals";
                case BASS:
                    return "Bass";
                case GUITAR:
                    return "Guitar";
                case PRO_BASS:
                    return "Pro Bass";
                case PRO_GUITAR:
                    return "Pro Guitar";
            }

            return "Unknown Instrument";
        }

        public async Task<Tuple<bool, List<LeaderboardData>>> GetLeaderboardsForInstrument(
            List<Song> items, 
            string accessToken, 
            string accountId, 
            int maxSeason, 
            List<LeaderboardData> prevData, 
            System.Windows.Forms.TextBox textBox,
            List<string> filteredSongIds)
        {
            if (items.Count == 0)
            {
                return new Tuple<bool, List<LeaderboardData>>(true, new List<LeaderboardData>());
            }

            var leaderboardData = new List<LeaderboardData>();
            int maxValidSeason = -1;

            // TO_DO: Season 1 doesn't exist yet
            foreach (Song song in items)
            {
                if (filteredSongIds.Count > 0 && !filteredSongIds.Contains(song.track.su))
                {
                    if (prevData.Find(x => x.songId == song.track.su) != null)
                    {
                        leaderboardData.Add(prevData.Find(x => x.songId == song.track.su));
                    }
                    continue;
                }
                
                var songBoard = new LeaderboardData();
                songBoard.title = song.track.tt;
                songBoard.artist = song.track.an;
                songBoard.songId = song.track.su;
                int minSongSeason = 2;

                var prevSongData = prevData.Find(x => x.songId == song.track.su);
                if (prevSongData == null)
                {
                    prevSongData = new LeaderboardData();
                }
                for (int i = 0; i < 6; i++)
                {
                    var prevInstrumentTracker = new ScoreTracker();
                    var instrumentName = "";
                    switch (i)
                    {
                        case 0:
                            instrumentName = DRUMS;
                            prevInstrumentTracker = prevSongData.drums ?? new ScoreTracker();
                            prevInstrumentTracker.difficulty = song.track.@in.ds;
                            break;
                        case 1:
                            instrumentName = GUITAR;
                            prevInstrumentTracker = prevSongData.guitar ?? new ScoreTracker();
                            prevInstrumentTracker.difficulty = song.track.@in.gr;
                            break;
                        case 2:
                            instrumentName = PRO_BASS;
                            prevInstrumentTracker = prevSongData.pro_bass ?? new ScoreTracker();
                            prevInstrumentTracker.difficulty = song.track.@in.pb;
                            break;
                        case 3:
                            instrumentName = PRO_GUITAR;
                            prevInstrumentTracker = prevSongData.pro_guitar ?? new ScoreTracker();
                            prevInstrumentTracker.difficulty = song.track.@in.pg;
                            break;
                        case 4:
                            instrumentName = BASS;
                            prevInstrumentTracker = prevSongData.bass ?? new ScoreTracker();
                            prevInstrumentTracker.difficulty = song.track.@in.ba;
                            break;
                        case 5:
                            instrumentName = VOCALS;
                            prevInstrumentTracker = prevSongData.vocals ?? new ScoreTracker();
                            prevInstrumentTracker.difficulty = song.track.@in.vl;
                            break;
                    }

                    var isSeasonActive = true;
                    var baseSeason = prevInstrumentTracker.lastSeenSeason == -1 ? ((instrumentName == PRO_BASS || instrumentName == PRO_GUITAR) ? Math.Max(minSongSeason, PRO_STRINGS_MINSEASON) : minSongSeason) : prevInstrumentTracker.lastSeenSeason;
                    var instrumentData = new ScoreTracker();
                    instrumentData.minSeason = prevInstrumentTracker.minSeason == -1 ? -1 : prevInstrumentTracker.minSeason;

                    instrumentData.maxScore = prevInstrumentTracker.maxScore;
                    instrumentData.percentHit = prevInstrumentTracker.percentHit;
                    instrumentData.isFullCombo = prevInstrumentTracker.isFullCombo;
                    instrumentData.numStars = prevInstrumentTracker.numStars;
                    instrumentData.initialized = prevInstrumentTracker.initialized;
                    instrumentData.season = prevInstrumentTracker.season;
                    instrumentData.difficulty = prevInstrumentTracker.difficulty;

                    var hasSeenValidLeaderboard = false;

                    while (isSeasonActive && baseSeason <= maxSeason && (maxValidSeason == -1 || baseSeason < maxValidSeason))
                    {
                        var seasonToString = baseSeason.ToString();
                        while (seasonToString.Length < 3)
                        {
                            seasonToString = "0" + seasonToString;
                        }

                        var client = new RestClient(API_BASE_ADDRESS + "season" + seasonToString + "_" + song.track.su + "/" + song.track.su + "_" + instrumentName + "/scores?accountId=" + accountId + "&" + PARAMS);
                        var request = new RestRequest();
                        request.Method = Method.Post;
                        request.AddHeader("Authorization", "bearer " + accessToken);
                        request.AddHeader("Accept-Encoding", "gzip, deflate, br");
                        request.AddHeader("Content-Type", "application/json");
                        request.AddHeader("Accept", "application/json");
                        request.AddParameter("", "{\"teams\":[[\"" + accountId + "\"]]}", ParameterType.RequestBody);

                        textBox.AppendText(Environment.NewLine + "Getting leaderboard for " + song.track.tt + " for " + GetFriendlyInstrumentName(instrumentName) + ", Season " + baseSeason);

                        var res = await client.ExecuteAsync(request);

                        try
                        {
                            var leaderboard = JsonConvert.DeserializeObject<ScoreList[]>(res.Content);
                            hasSeenValidLeaderboard = true;
                            if (instrumentData.minSeason == -1)
                            {
                                instrumentData.minSeason = baseSeason;
                            }
                            instrumentData.lastSeenSeason = baseSeason;

                            if (leaderboard != null)
                            {
                                foreach (ScoreList score in leaderboard)
                                {
                                    if (score.score > instrumentData.maxScore)
                                    {
                                        instrumentData.maxScore = score.score;
                                        instrumentData.season = baseSeason;
                                        instrumentData.initialized = true;
                                        foreach (var item in score.sessionHistory)
                                        {
                                            if (item.trackedStats.SCORE == score.score)
                                            {
                                                instrumentData.percentHit = item.trackedStats.ACCURACY;
                                                instrumentData.isFullCombo = item.trackedStats.FULL_COMBO == 1;
                                                instrumentData.numStars = item.trackedStats.STARS_EARNED;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            var error = JsonConvert.DeserializeObject<InvalidSeason>(res.Content);
                            if (error.errorCode == "com.epicgames.events.unauthorized")
                            {
                                textBox.AppendText(Environment.NewLine + "The access token being used has become or always was unauthorized. Make sure you have not logged into Fortnite since beginning and try again.");
                                return new Tuple<bool, List<LeaderboardData>>(false, new List<LeaderboardData>());
                            }
                            if (error.errorCode == "com.epicgames.events.no_score_found")
                            {
                                if (instrumentData.minSeason == -1)
                                {
                                    instrumentData.minSeason = baseSeason;
                                }
                                hasSeenValidLeaderboard = true;
                                instrumentData.lastSeenSeason = baseSeason;
                                baseSeason++;
                                continue;
                            }
                            if (error.errorCode == "com.epicgames.events.invalid_leaderboard")
                            {
                                if (hasSeenValidLeaderboard)
                                {
                                    instrumentData.lastSeenSeason = baseSeason - 1;
                                    isSeasonActive = false;
                                    if (baseSeason > maxValidSeason)
                                    {
                                        maxValidSeason = baseSeason;
                                    }
                                }
                                else
                                {
                                    minSongSeason++;
                                }
                            }
                            else
                            {
                                textBox.AppendText(Environment.NewLine + "An unexpected error has occurred. Please report this error to the GitHub repo so it can be fixed.");
                                textBox.AppendText(Environment.NewLine + "Error Code: " + error.errorCode);
                                textBox.AppendText(Environment.NewLine + "Error Message: " + error.errorMessage);
                                return new Tuple<bool, List<LeaderboardData>>(false, new List<LeaderboardData>());
                            }
                        }

                        baseSeason++;
                    }

                    switch (i)
                    {
                        case 0:
                            songBoard.drums = instrumentData;
                            break;
                        case 1:
                            songBoard.guitar = instrumentData;
                            break;
                        case 2:
                            songBoard.pro_bass = instrumentData;
                            break;
                        case 3:
                            songBoard.pro_guitar = instrumentData;
                            break;
                        case 4:
                            songBoard.bass = instrumentData;
                            break;
                        case 5:
                            songBoard.vocals = instrumentData;
                            break;
                    }
                }

                leaderboardData.Add(songBoard);
            }

            return new Tuple<bool, List<LeaderboardData>>(true, leaderboardData);
        }
    }
}