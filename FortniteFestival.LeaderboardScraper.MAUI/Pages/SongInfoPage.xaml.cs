using Microsoft.Maui.Controls;
using FortniteFestival.LeaderboardScraper.MAUI.ViewModels;

namespace FortniteFestival.LeaderboardScraper.MAUI.Pages;

public partial class SongInfoPage : ContentPage
{
    public SongInfoPage(SongInfoViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
        // Apply initial width adaptation (ensures ShowScore/ShowPercent/ShowSeason flags also set)
        vm.AdaptForWidth(Width);
        SizeChanged += (_, _) => AdaptForWidth();
    }

    private void AdaptForWidth()
    {
        if (BindingContext is SongInfoViewModel vm)
        {
            // Use viewmodel's richer adaptation logic instead of only toggling compact flag
            vm.AdaptForWidth(Width);
        }
    }

    private async void OnBackTapped(object sender, TappedEventArgs e)
    {
        try { await Navigation.PopAsync(); } catch { }
    }
}
