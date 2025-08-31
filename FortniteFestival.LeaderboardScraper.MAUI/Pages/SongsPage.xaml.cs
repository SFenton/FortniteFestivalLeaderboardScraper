using FortniteFestival.Core;
using FortniteFestival.LeaderboardScraper.MAUI.ViewModels;

namespace FortniteFestival.LeaderboardScraper.MAUI.Pages;

public partial class SongsPage : ContentPage
{
    private readonly SongsViewModel _vm;

    public SongsPage(SongsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.Refresh();
    }

    private void OnCheckChanged(object sender, CheckedChangedEventArgs e)
    {
        if (sender is CheckBox cb && cb.BindingContext is Song s)
        {
            _vm.ToggleSelection(s);
        }
    }
}
