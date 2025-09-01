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
    }

    private void OnSizeChanged(object? sender, EventArgs e)
    {
        Vm?.AdaptForWidth(Width);
    }

    private async void OnBackTapped(object? sender, TappedEventArgs e)
    {
        await Navigation.PopAsync();
    }
}
