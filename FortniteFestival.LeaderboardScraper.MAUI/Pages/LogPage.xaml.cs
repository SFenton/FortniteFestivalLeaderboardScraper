using FortniteFestival.LeaderboardScraper.MAUI.ViewModels;

namespace FortniteFestival.LeaderboardScraper.MAUI.Pages;

public partial class LogPage : ContentPage
{
    public LogPage(ProcessViewModel processVm)
    {
        InitializeComponent();
        BindingContext = processVm; // reuse log from process vm
    }
}
