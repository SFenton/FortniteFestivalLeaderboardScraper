using System.Collections.ObjectModel;
using FortniteFestival.Core;

namespace FortniteFestival.LeaderboardScraper.MAUI;

public class AppState
{
    public ObservableCollection<Song> Songs { get; } = new ObservableCollection<Song>();
    public ObservableCollection<LeaderboardData> Scores { get; } = new ObservableCollection<LeaderboardData>();
    public string ExchangeCode { get; set; } = string.Empty;
    public bool IsInitializing { get; set; }
    public bool IsFetching { get; set; }
    public int ProgressCurrent { get; set; }
    public int ProgressTotal { get; set; }
    public List<string> SelectedSongIds { get; } = new List<string>();
    public void ResetProgress(){ ProgressCurrent = 0; ProgressTotal = 0; }
}
