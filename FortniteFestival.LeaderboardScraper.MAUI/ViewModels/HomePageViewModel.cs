namespace FortniteFestival.LeaderboardScraper.MAUI.ViewModels;

public class HomePageViewModel : BaseViewModel
{
    public ProcessViewModel Process { get; }
    public SongsViewModel Songs { get; }
    public HomePageViewModel(ProcessViewModel process, SongsViewModel songs)
    {
        Process = process;
        Songs = songs;
    }
}
