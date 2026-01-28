using Microsoft.Maui.Controls;
using FortniteFestival.LeaderboardScraper.MAUI.ViewModels;

namespace FortniteFestival.LeaderboardScraper.MAUI.Pages;

public partial class SongInfoPage : ContentPage
{
    private bool _isNavigatingBack;
    
    public SongInfoPage(SongInfoViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
        // Apply initial width adaptation (ensures ShowScore/ShowPercent/ShowSeason flags also set)
        vm.AdaptForWidth(Width);
        SizeChanged += (_, _) => AdaptForWidth();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _isNavigatingBack = false; // Reset when page appears
        // Check if this song should be prioritized during an active fetch
        if (BindingContext is SongInfoViewModel vm)
        {
            vm.CheckAndPrioritizeIfNeeded();
        }
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
        // Prevent multiple taps triggering multiple navigations
        if (_isNavigatingBack) return;
        _isNavigatingBack = true;
        
        try { await Navigation.PopAsync(); }
        catch (Exception ex) 
        { 
            System.Diagnostics.Debug.WriteLine($"[SongInfoPage] Error navigating back: {ex.Message}");
            _isNavigatingBack = false; // Reset on error so user can try again
        }
    }
}
