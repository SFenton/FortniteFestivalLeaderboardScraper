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

        // Cached formatted strings (avoids repeated formatting on UI threads)
        public string percentHitFormatted { get; set; }
        public string starsFormatted { get; set; }

        public void RefreshDerived()
        {
            percentHitFormatted = (percentHit / 10000.0).ToString("0.00") + "%";
            if (numStars <= 0)
                starsFormatted = "N/A";
            else
                starsFormatted = new string('?', Math.Min(numStars, 6));
        }
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

        // Mark when any tracker updated to avoid unnecessary DB writes
        public bool dirty { get; set; }
    }
}
