using System.Collections.Generic;

namespace FortniteFestival.Core.Suggestions
{
    /// <summary>Which rival system produced the data.</summary>
    public enum RivalSourceKind
    {
        Song,
        Leaderboard,
    }

    /// <summary>Summary info about a single rival player.</summary>
    public class RivalInfo
    {
        public string AccountId { get; set; }
        public string DisplayName { get; set; }
        public string Direction { get; set; } // "above" or "below"
        public RivalSourceKind Source { get; set; }
        public int SharedSongCount { get; set; }
        public int AheadCount { get; set; }
        public int BehindCount { get; set; }
    }

    /// <summary>A per-song match between the user and a rival.</summary>
    public class RivalSongMatch
    {
        public RivalInfo Rival { get; set; }
        public string SongId { get; set; }
        public string Instrument { get; set; }
        public int UserRank { get; set; }
        public int RivalRank { get; set; }
        /// <summary>userRank - rivalRank. Negative = rival ahead.</summary>
        public int RankDelta { get; set; }
        public int? UserScore { get; set; }
        public int? RivalScore { get; set; }
    }

    /// <summary>Indexed lookup structure for rival queries in the suggestion generator.</summary>
    public class RivalDataIndex
    {
        public List<RivalInfo> SongRivals { get; set; } = new();
        public List<RivalInfo> LeaderboardRivals { get; set; } = new();
        public List<RivalInfo> AllRivals { get; set; } = new();
        /// <summary>All song matches per rival: rivalAccountId → list of matches.</summary>
        public Dictionary<string, List<RivalSongMatch>> ByRival { get; set; } = new();
        /// <summary>Closest rival match per song+instrument: "songId:instrument" → match.</summary>
        public Dictionary<string, RivalSongMatch> ClosestRivalBySong { get; set; } = new();
        /// <summary>Quick leaderboard rival lookup: accountId → info.</summary>
        public Dictionary<string, RivalInfo> LeaderboardRivalIndex { get; set; } = new();
    }
}
