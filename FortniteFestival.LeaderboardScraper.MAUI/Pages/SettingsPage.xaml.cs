using FortniteFestival.Core;
using FortniteFestival.Core.Services;

namespace FortniteFestival.LeaderboardScraper.MAUI.Pages;

public partial class SettingsPage : ContentPage
{
    private readonly IFestivalService _service;
    public SettingsPage(IFestivalService service)
    {
        InitializeComponent();
        _service = service;
    UpdateSuggestionsVisibility();
        try { _service.ScoreUpdated += OnScoreUpdated; } catch { }
    }

    private void OnScoreUpdated(LeaderboardData obj) => MainThread.BeginInvokeOnMainThread(UpdateSuggestionsVisibility);

    private void UpdateSuggestionsVisibility()
    {
        bool hasScore = false;
        try { hasScore = _service.ScoresIndex != null && _service.ScoresIndex.Count > 0; } catch { }
        if (SuggestionsNavItem != null) SuggestionsNavItem.IsVisible = hasScore;
    }

    private async void OnHamburgerTapped(object sender, TappedEventArgs e)
    {
        await AnimatePressAsync(HamburgerButton);
        NavDrawerOverlay.IsVisible = true;
        UpdateSuggestionsVisibility();
    }

    private void OnCloseDrawerTapped(object sender, TappedEventArgs e)
    { NavDrawerOverlay.IsVisible = false; }

    private async void OnNavSongsTapped(object sender, TappedEventArgs e)
    { await AnimatePressAsync((VisualElement)sender); NavDrawerOverlay.IsVisible = false; await Navigation.PopToRootAsync(); }

    private async void OnNavSuggestionsTapped(object sender, TappedEventArgs e)
    { await AnimatePressAsync((VisualElement)sender); NavDrawerOverlay.IsVisible = false; await Navigation.PushAsync(new SuggestionsPage(_service)); }

    private async void OnNavStatisticsTapped(object sender, TappedEventArgs e)
    { await AnimatePressAsync((VisualElement)sender); NavDrawerOverlay.IsVisible = false; await Navigation.PushAsync(new StatisticsPage(_service)); }

    private async void OnNavSettingsTapped(object sender, TappedEventArgs e)
    { await AnimatePressAsync((VisualElement)sender); NavDrawerOverlay.IsVisible = false; /* already here */ }

    private static async Task AnimatePressAsync(VisualElement element)
    {
        if (element == null) return; try { await element.ScaleTo(0.95, 70, Easing.CubicIn); await element.ScaleTo(1, 70, Easing.CubicOut); } catch { }
    }
}
