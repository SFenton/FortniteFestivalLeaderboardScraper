using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FortniteFestivalLeaderboardScraper.Helpers
{
    // Root myDeserializedClass = JsonConvert.DeserializeObject<List<Root>>(myJsonResponse);
    public class PointBreakdown
    {
        [JsonProperty("SCORE:1")]
        public SCORE1 SCORE1 { get; set; }
    }

    public class LeaderboardEntry
    {
        public ScoreKey scoreKey { get; set; }
        public string teamId { get; set; }
        public List<string> teamAccountIds { get; set; }
        public int pointsEarned { get; set; }
        public int score { get; set; }
        public int rank { get; set; }
        public int percentile { get; set; }
        public PointBreakdown pointBreakdown { get; set; }
        public List<SessionHistory> sessionHistory { get; set; }
        public List<object> unscoredSessions { get; set; }
    }

    public class SCORE1
    {
        public int timesAchieved { get; set; }
        public int pointsEarned { get; set; }
    }

    public class ScoreKey
    {
        public string gameId { get; set; }
        public string eventId { get; set; }
        public string eventWindowId { get; set; }
    }

    public class SessionHistory
    {
        public string sessionId { get; set; }
        public DateTime endTime { get; set; }
        public TrackedStats trackedStats { get; set; }
    }

    public class TrackedStats
    {
        public int B_SCORE { get; set; }
        public int SCORE { get; set; }
        public int M_1_INSTRUMENT { get; set; }
        public int M_0_ACCURACY { get; set; }
        public int M_0_DIFFICULTY { get; set; }
        public int B_STARS { get; set; }
        public int M_0_INSTRUMENT { get; set; }
        public int B_BASESCORE { get; set; }
        public int M_0_SCORE { get; set; }
        public int M_1_DIFFICULTY { get; set; }
        public int B_INSTRUMENT_BONUS { get; set; }
        public int ACCURACY { get; set; }
        public int INSTRUMENT_0 { get; set; }
        public int M_1_STARS_EARNED { get; set; }
        public int M_1_FULL_COMBO { get; set; }
        public int STARS_EARNED { get; set; }
        public int B_FULL_COMBO { get; set; }
        public int B_OVERDRIVE_BONUS { get; set; }
        public int DIFFICULTY { get; set; }
        public int M_1_ACCURACY { get; set; }
        public int M_0_STARS_EARNED { get; set; }
        public int B_ACCURACY { get; set; }
        public int M_0_FULL_COMBO { get; set; }
        public int B_MODIFIER_BONUS { get; set; }
        public int M_1_ID_8d72145a68f74c2c8d44a70c2eb8840a { get; set; }
        public int FULL_COMBO { get; set; }
        public int M_0_ID_195e93ef108143b2975ee46662d4d0e1 { get; set; }
        public int M_1_SCORE { get; set; }
        public int? M_3_FULL_COMBO { get; set; }
        public int? M_3_DIFFICULTY { get; set; }
        public int? M_2_SCORE { get; set; }
        public int? M_3_ACCURACY { get; set; }
        public int? M_3_STARS_EARNED { get; set; }
        public int? M_0_ID_108a4dae881e42a38554ad0ed844cb67 { get; set; }
        public int? M_2_FULL_COMBO { get; set; }
        public int? M_2_INSTRUMENT { get; set; }
        public int? M_2_ID_8406e9d31026404ebbc878bd0ca9ff3e { get; set; }
        public int? M_3_INSTRUMENT { get; set; }
        public int? M_2_ACCURACY { get; set; }
        public int? M_1_ID_2af1c90eb1174a2e8fd2631eb9bcf1b8 { get; set; }
        public int? M_3_SCORE { get; set; }
        public int? M_2_DIFFICULTY { get; set; }
        public int? M_3_ID_25ce50c0838940029ce958d4c388cb52 { get; set; }
        public int? M_2_STARS_EARNED { get; set; }
        public int? SEASON { get; set; }
    }

    // New root type for the all-time leaderboard endpoint (v1 api) so we can instrument calls
    public class AllTimeLeaderboardPage
    {
        public string gameId { get; set; }
        public string eventId { get; set; }
        public string eventWindowId { get; set; }
        public int page { get; set; }
        public int totalPages { get; set; }
        public DateTime updatedTime { get; set; }
        public List<LeaderboardEntry> entries { get; set; }
        public Dictionary<string, object> liveSessions { get; set; }
    }
}
