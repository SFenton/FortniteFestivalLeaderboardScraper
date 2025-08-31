using FortniteFestival.LeaderboardScraper.MAUI.ViewModels;

namespace FortniteFestival.LeaderboardScraper.MAUI.Pages;

public partial class ProcessPage : ContentPage
{
    private readonly ProcessViewModel _vm;
    private bool _logSubscribed;
    public ProcessPage(ProcessViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.EnsureInitializedAsync();
        if(!_logSubscribed)
        {
            _vm.LogLines.CollectionChanged += (s,e)=> MainThread.BeginInvokeOnMainThread(()=>
            {
                if(_vm.LogLines.Count>0)
                    LogCollectionView.ScrollTo(_vm.LogLines[^1], position: ScrollToPosition.End, animate: true);
            });
            _logSubscribed = true;
        }
    }
}
