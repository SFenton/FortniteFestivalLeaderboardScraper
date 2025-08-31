using FortniteFestival.LeaderboardScraper.MAUI.ViewModels;

namespace FortniteFestival.LeaderboardScraper.MAUI.Pages;

public partial class ScoresPage : ContentPage
{
    private readonly ScoresViewModel _vm;
    public ScoresPage(ScoresViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.RefreshFromService();
    }

    void OnInstrumentChanged(object sender, CheckedChangedEventArgs e)
    {
        if(!e.Value) return;
        if(sender is RadioButton rb && rb.Content is string name)
        {
            _vm.Instrument = name;
        }
    }
}
