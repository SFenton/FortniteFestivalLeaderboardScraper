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
    }
}