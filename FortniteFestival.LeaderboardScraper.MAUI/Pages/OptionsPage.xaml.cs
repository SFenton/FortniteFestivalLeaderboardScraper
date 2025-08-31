using FortniteFestival.LeaderboardScraper.MAUI.ViewModels;

namespace FortniteFestival.LeaderboardScraper.MAUI.Pages;

public partial class OptionsPage : ContentPage
{
    public OptionsPage(OptionsViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
