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
    // Global leaderboard rank (1 = best). 0 means unknown/not fetched.
    public int rank { get; set; }
    // Approximate total entries in the all-time leaderboard for this instrument (0 when unknown)
    public int totalEntries { get; set; }
    // Raw percentile value returned directly by the API when available (often small fraction). -1 or 0 when unknown.
    public double rawPercentile { get; set; }
    // Reverse-calculated total leaderboard entries using rank/rawPercentile (rounded). 0 when unknown.
    public int calculatedNumEntries { get; set; }

        // Cached formatted strings (avoids repeated formatting on UI threads)
        public string percentHitFormatted { get; set; }
        public string starsFormatted { get; set; }
        public string leaderboardPercentileFormatted { get; set; }

        public void RefreshDerived()
        {
            percentHitFormatted = (percentHit / 10000.0).ToString("0.00") + "%";
            if (numStars <= 0)
                starsFormatted = "N/A";
            else
                starsFormatted = new string('?', Math.Min(numStars, 6));
            // Derive formatted percentile string from rawPercentile (fraction ~ rank/total; smaller is better)
            if (rawPercentile > 0)
            {
                double topPct = rawPercentile * 100.0; // convert to percentage (e.g., 0.0144 => 1.44%)
                if (topPct < 0) topPct = 0; if (topPct > 100) topPct = 100;
                if (topPct < 1.0) topPct = 1.0; // normalize to at least Top 1%
                int[] thresholds = { 1, 2, 3, 4, 5, 10, 15, 20, 25, 30, 40, 50, 60, 70, 80, 90, 100 };
                int bucket = 100;
                for (int i = 0; i < thresholds.Length; i++)
                {
                    if (topPct <= thresholds[i]) { bucket = thresholds[i]; break; }
                }
                leaderboardPercentileFormatted = "Top " + bucket + "%";
            }
            else
            {
                leaderboardPercentileFormatted = "";
            }
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

        // Transient correlation: recent v1 leaderboard page slices per instrument key (e.g. "Guitar", "Drums"). Not persisted.
        [System.Text.Json.Serialization.JsonIgnore]
        public System.Collections.Generic.Dictionary<string, V1LeaderboardPage> correlatedV1Pages { get; set; }
    }

    // Minimal DTOs for v1 leaderboard correlation (fields we currently use only)
    public class V1LeaderboardPage
    {
        public int page { get; set; }
        public int totalPages { get; set; }
        public System.Collections.Generic.List<V1LeaderboardEntry> entries { get; set; }
    }

    public class V1LeaderboardEntry
    {
        public string team_id { get; set; }
        public int rank { get; set; }
        public int pointsEarned { get; set; }
        public int score { get; set; } // derived (highest SCORE from sessionHistory)
    public double percentile { get; set; } // raw percentile value from v1 (if supplied)
        public System.Collections.Generic.List<V1SessionHistory> sessionHistory { get; set; }
    }

    public class V1SessionHistory
    {
        public string endTime { get; set; }
        public V1TrackedStats trackedStats { get; set; }
    }

    public class V1TrackedStats
    {
        public int SCORE { get; set; }
        public int ACCURACY { get; set; }
        public int FULL_COMBO { get; set; }
        public int STARS_EARNED { get; set; }
    public int SEASON { get; set; } // added: season index achieved for this score (if provided by API)
    }
}
