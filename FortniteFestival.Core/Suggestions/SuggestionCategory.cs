using System.Collections.Generic;

namespace FortniteFestival.Core.Suggestions
{
    public class SuggestionCategory
    {
        public string Key { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public List<SuggestionSongItem> Songs { get; set; } = new List<SuggestionSongItem>();
    }

    public class SuggestionSongItem
    {
        public string SongId { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public int? Stars { get; set; }
        public double? Percent { get; set; }
        public bool? FullCombo { get; set; }
        /// <summary>Display name of the closest rival on this song (from cross-pollination).</summary>
        public string RivalName { get; set; }
        /// <summary>Account ID for navigation to rival detail.</summary>
        public string RivalAccountId { get; set; }
        /// <summary>Signed rank delta vs the rival (negative = they lead).</summary>
        public int? RivalRankDelta { get; set; }
        /// <summary>Which rival system: "song" or "leaderboard".</summary>
        public string RivalSource { get; set; }
    }
}