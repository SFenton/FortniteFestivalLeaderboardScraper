using FortniteFestival.LeaderboardScraper.MAUI.ViewModels;

namespace FortniteFestival.LeaderboardScraper.MAUI.Pages;

public partial class LogPage : ContentPage
{
    private readonly ProcessViewModel _vm;

    public LogPage(ProcessViewModel processVm)
    {
        InitializeComponent();
        BindingContext = _vm = processVm; // reuse log from process vm
    }

    private async void OnCopyAll(object sender, EventArgs e)
    {
        try
        {
            await Clipboard.SetTextAsync(_vm.LogJoined);
        }
        catch { }
    }
}
