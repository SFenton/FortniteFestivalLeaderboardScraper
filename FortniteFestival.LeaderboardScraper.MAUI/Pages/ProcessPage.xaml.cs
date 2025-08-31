using FortniteFestival.LeaderboardScraper.MAUI.ViewModels;

namespace FortniteFestival.LeaderboardScraper.MAUI.Pages;

public partial class ProcessPage : ContentPage
{
    private readonly ProcessViewModel _vm;

    public ProcessPage(ProcessViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.EnsureInitializedAsync();
    }

    private async void OnCopyLog(object sender, EventArgs e)
    {
        try
        {
            await Clipboard.SetTextAsync(_vm.LogJoined);
        }
        catch { }
    }
}
