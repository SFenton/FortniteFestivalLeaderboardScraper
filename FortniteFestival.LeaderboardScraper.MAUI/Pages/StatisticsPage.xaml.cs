using FortniteFestival.Core.Services;
using FortniteFestival.Core;

namespace FortniteFestival.LeaderboardScraper.MAUI.Pages;

public partial class StatisticsPage : ContentPage
{
    private readonly IFestivalService _service;
    public StatisticsPage(IFestivalService service)
    {
        InitializeComponent();
        _service = service;
        ApplyState();
    UpdateSuggestionsVisibility();
    try { _service.ScoreUpdated += OnScoreUpdated; } catch { }
    }

    private void ApplyState()
    {
        bool hasAny = _service.ScoresIndex != null && _service.ScoresIndex.Count > 0;
        EmptyState.IsVisible = !hasAny;
        StatsStack.IsVisible = hasAny;
        if (!hasAny) return;
        StatsStack.Children.Clear();
        // Placeholder content
        StatsStack.Children.Add(new Label { Text = "Statistics coming soon...", FontSize = 16, FontAttributes = FontAttributes.Italic });
    }

    private void OnScoreUpdated(LeaderboardData obj) => MainThread.BeginInvokeOnMainThread(() => { ApplyState(); UpdateSuggestionsVisibility(); });
    private void UpdateSuggestionsVisibility()
    {
        bool hasScore = false; try { hasScore = _service.ScoresIndex != null && _service.ScoresIndex.Count > 0; } catch { }
        if (SuggestionsNavItem != null) SuggestionsNavItem.IsVisible = hasScore;
    }
    private async void OnHamburgerTapped(object sender, TappedEventArgs e) { await AnimatePressAsync(HamburgerButton); NavDrawerOverlay.IsVisible = true; UpdateSuggestionsVisibility(); }
    private void OnCloseDrawerTapped(object sender, TappedEventArgs e) { NavDrawerOverlay.IsVisible = false; }
    private async void OnNavSongsTapped(object sender, TappedEventArgs e) { await AnimatePressAsync((VisualElement)sender); NavDrawerOverlay.IsVisible = false; await Navigation.PopToRootAsync(); }
    private async void OnNavSuggestionsTapped(object sender, TappedEventArgs e) { await AnimatePressAsync((VisualElement)sender); NavDrawerOverlay.IsVisible = false; await Navigation.PushAsync(new SuggestionsPage(_service)); }
    private async void OnNavStatisticsTapped(object sender, TappedEventArgs e) { await AnimatePressAsync((VisualElement)sender); NavDrawerOverlay.IsVisible = false; /* already here */ }
    private async void OnNavSettingsTapped(object sender, TappedEventArgs e) { await AnimatePressAsync((VisualElement)sender); NavDrawerOverlay.IsVisible = false; await Navigation.PushAsync(new SettingsPage(_service)); }
    private static async Task AnimatePressAsync(VisualElement element) { if (element == null) return; try { await element.ScaleTo(0.95,70,Easing.CubicIn); await element.ScaleTo(1,70,Easing.CubicOut); } catch { } }
}
