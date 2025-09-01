using FortniteFestival.Core;
using FortniteFestival.Core.Services;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Xaml;

namespace FortniteFestival.LeaderboardScraper.MAUI.Controls;
// Allow <controls:DrawerLayout><controls:DrawerLayout.PageContent>...</controls:DrawerLayout.PageContent></controls:DrawerLayout>
// and also shorthand content usage in XAML if desired in future.
[ContentProperty(nameof(PageContent))]
public partial class DrawerLayout : ContentView
{
    public static readonly BindableProperty ServiceProperty = BindableProperty.Create(
        nameof(Service), typeof(IFestivalService), typeof(DrawerLayout), propertyChanged:(b,o,n)=>((DrawerLayout)b).UpdateSuggestionsVisibility());

    public IFestivalService? Service
    {
        get => (IFestivalService?)GetValue(ServiceProperty);
        set => SetValue(ServiceProperty, value);
    }

    public static readonly BindableProperty PageContentProperty = BindableProperty.Create(
        nameof(PageContent), typeof(View), typeof(DrawerLayout), default(View), propertyChanged: OnPageContentChanged);

    private static void ApplyPageContentToHost(DrawerLayout layout, View? v)
    {
        if (layout.ContentHost == null)
        {
            // host not ready yet; will re-apply in constructor after InitializeComponent
            layout._pendingPageContent = v;
            return;
        }
        layout.ContentHost.Children.Clear();
        if (v != null) layout.ContentHost.Children.Add(v);
    }

    public View? PageContent
    {
        get => (View?)GetValue(PageContentProperty);
        set => SetValue(PageContentProperty, value);
    }

    private static void OnPageContentChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var layout = (DrawerLayout)bindable;
        ApplyPageContentToHost(layout, newValue as View);
    }

    private View? _pendingPageContent;

    public DrawerLayout()
    {
        InitializeComponent();
        // If the property was set during XAML load before ContentHost existed, apply now.
        if (_pendingPageContent != null)
        {
            ApplyPageContentToHost(this, _pendingPageContent);
            _pendingPageContent = null;
        }
        else if (PageContent is View v)
        {
            ApplyPageContentToHost(this, v);
        }
    }

    protected override void OnParentSet()
    {
        base.OnParentSet();
        if (Service != null)
            try { Service.ScoreUpdated += OnScoreUpdated; } catch { }
        UpdateSuggestionsVisibility();
    }

    private void OnScoreUpdated(LeaderboardData l) => MainThread.BeginInvokeOnMainThread(UpdateSuggestionsVisibility);

    private void UpdateSuggestionsVisibility()
    {
        bool has = false;
        try { has = Service?.ScoresIndex?.Count > 0; } catch { }
        if (SuggestionsNavItem != null) SuggestionsNavItem.IsVisible = has;
    }

    private async Task AnimatePressAsync(VisualElement element)
    {
        if (element == null) return;
        try { await element.ScaleTo(0.95,70,Easing.CubicIn); await element.ScaleTo(1,70,Easing.CubicOut); } catch { }
    }

    private async void OnHamburgerTapped(object sender, TappedEventArgs e)
    {
        await AnimatePressAsync(HamburgerButton);
        NavDrawerOverlay.IsVisible = true;
        if (DrawerPanel != null)
        {
            DrawerPanel.TranslationX = -DrawerPanel.Width;
            await DrawerPanel.TranslateTo(0,0,180,Easing.CubicOut);
        }
        UpdateSuggestionsVisibility();
    }

    private async void OnCloseDrawerTapped(object sender, TappedEventArgs e)
    {
        if (DrawerPanel != null)
            await DrawerPanel.TranslateTo(-DrawerPanel.Width,0,160,Easing.CubicIn);
        NavDrawerOverlay.IsVisible = false;
    }

    // Navigation events delegate to hosting page's Navigation stack
    private async void OnNavSongsTapped(object sender, TappedEventArgs e)
    {
        await AnimatePressAsync((VisualElement)sender);
        await CloseAsync();
        var nav = GetParentPage()?.Navigation;
        if (nav == null) return;
        try
        {
            // If root is already SongsPage just pop; otherwise pop then push a new SongsPage (requires service from ancestor implementing it)
            await nav.PopToRootAsync();
            // If root isn't SongsPage (e.g., app opened directly to Suggestions), try to locate a SongsPage in stack; otherwise leave as-is
            var root = nav.NavigationStack.FirstOrDefault();
            // Optional: could resolve from DI container if accessible; for now we avoid manual construction to preserve state.
        }
        catch { }
    }

    private async void OnNavSuggestionsTapped(object sender, TappedEventArgs e)
    {
        await AnimatePressAsync((VisualElement)sender);
        if (Service == null) return;
        await CloseAsync();
        var parent = GetParentPage();
        if (parent != null)
        {
            // Avoid duplicating SuggestionsPage if already active
            var nav = parent.Navigation;
            var current = nav?.NavigationStack.LastOrDefault();
            if (current is not Pages.SuggestionsPage)
            {
                await parent.Navigation.PushAsync(new Pages.SuggestionsPage(Service));
            }
        }
    }
    private async void OnNavStatisticsTapped(object sender, TappedEventArgs e)
    {
        await AnimatePressAsync((VisualElement)sender);
        if (Service == null) return;
        await CloseAsync();
        var parent = GetParentPage();
        if (parent != null) await parent.Navigation.PushAsync(new Pages.StatisticsPage(Service));
    }
    private async void OnNavSettingsTapped(object sender, TappedEventArgs e)
    {
        await AnimatePressAsync((VisualElement)sender);
        if (Service == null) return;
        await CloseAsync();
        var parent = GetParentPage();
        if (parent != null) await parent.Navigation.PushAsync(new Pages.SettingsPage(Service));
    }

    private async Task CloseAsync()
    {
        if (DrawerPanel != null)
            await DrawerPanel.TranslateTo(-DrawerPanel.Width,0,160,Easing.CubicIn);
        NavDrawerOverlay.IsVisible = false;
    }

    private Page? GetParentPage()
    {
        Element? e = this;
        while (e != null && e is not Page) e = e.Parent;
        return e as Page;
    }
}
