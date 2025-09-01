using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Maui.Controls;
using FortniteFestival.LeaderboardScraper.MAUI.ViewModels;

namespace FortniteFestival.LeaderboardScraper.MAUI.Pages;

public partial class SongInfoPage : ContentPage
{
    public SongInfoViewModel Vm { get; }

    public SongInfoPage(SongInfoViewModel vm)
    {
        InitializeComponent();
        Vm = vm;
        BindingContext = vm;
    NavigationPage.SetHasNavigationBar(this, false);
        SizeChanged += OnSizeChanged;
    BackButton.SizeChanged += BackButtonOnSizeChanged;
    }

    private void OnSizeChanged(object? sender, EventArgs e)
    {
        Vm?.AdaptForWidth(Width);
    }

    private void BackButtonOnSizeChanged(object? sender, EventArgs e)
    {
        // Adjust content grid top padding so scrolling content begins just below the floating back button.
        if (ContentGrid != null && BackButton != null)
        {
            // Base top padding: back button height + its top margin + a small gap (8)
            double topPad = BackButton.Height + BackButton.Margin.Top + 8;
            var current = ContentGrid.Padding;
            if (Math.Abs(current.Top - topPad) > 0.5)
            {
                ContentGrid.Padding = new Thickness(current.Left, topPad, current.Right, current.Bottom);
            }
        }
    }

    private async void OnBackTapped(object? sender, TappedEventArgs e)
    {
        if (BackButton != null)
        {
            await AnimatePressAsync(BackButton);
        }
        await Navigation.PopAsync();
    }

    private static async Task AnimatePressAsync(VisualElement element)
    {
        if (element == null) return;
        try
        {
            uint duration = 60;
            await element.ScaleTo(0.975, duration, Easing.CubicIn);
            await element.ScaleTo(1.0, duration, Easing.CubicOut);
        }
        catch { }
    }
}
