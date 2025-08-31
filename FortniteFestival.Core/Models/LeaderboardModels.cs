using System;

namespace FortniteFestival.Core
{
    public class ScoreTracker
    {
        public bool initialized { get; set; }
        public int maxScore { get; set; }
        public int difficulty { get; set; }
        public int numStars { get; set; }
        public bool isFullCombo { get; set; }
        public int percentHit { get; set; }
        public int seasonAchieved { get; set; }
    }

    public class LeaderboardData
    {
        public string title { get; set; }
        public string artist { get; set; }
        public string songId { get; set; }
        public ScoreTracker drums { get; set; }
        public ScoreTracker guitar { get; set; }
        public ScoreTracker bass { get; set; }
        public ScoreTracker vocals { get; set; }
        public ScoreTracker pro_guitar { get; set; }
        public ScoreTracker pro_bass { get; set; }
    }
}
