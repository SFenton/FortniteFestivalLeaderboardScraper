namespace FortniteFestivalLeaderboardScraper.Helpers
{
    public class InvalidSeason
    {
        public string errorCode
        {
            get;
            set;
        }
        public string errorMessage
        {
            get;
            set;
        }
    }

    public class LeaderboardData
    {
        public string title
        {
            get;
            set;
        }
        public string artist
        {
            get;
            set;
        }
        public string songId
        {
            get;
            set;
        }
        //public Season[] seasons { get; set; }

        public ScoreTracker drums
        {
            get;
            set;
        }
        public ScoreTracker guitar
        {
            get;
            set;
        }
        public ScoreTracker bass
        {
            get;
            set;
        }
        public ScoreTracker vocals
        {
            get;
            set;
        }
        public ScoreTracker pro_guitar
        {
            get;
            set;
        }
        public ScoreTracker pro_bass
        {
            get;
            set;
        }
    }

    public class Season
    {
        public ScoreTracker drums
        {
            get;
            set;
        }
        public ScoreTracker guitar
        {
            get;
            set;
        }
        public ScoreTracker bass
        {
            get;
            set;
        }
        public ScoreTracker vocals
        {
            get;
            set;
        }
        public ScoreTracker pro_guitar
        {
            get;
            set;
        }
        public ScoreTracker pro_bass
        {
            get;
            set;
        }
    }

    public class ScoreTracker
    {
        public bool initialized
        {
            get;
            set;
        }
        public int maxScore
        {
            get;
            set;
        }
        public int difficulty
        {
            get;
            set;
        }
        public int numStars
        {
            get;
            set;
        }
        public bool isFullCombo
        {
            get;
            set;
        }
        public int percentHit
        {
            get;
            set;
        }
        public int season
        {
            get;
            set;
        }
        public int minSeason
        {
            get;
            set;
        } = -1;
        public int lastSeenSeason
        {
            get;
            set;
        } = -1;
    }
}
