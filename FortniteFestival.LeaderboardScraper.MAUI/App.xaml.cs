namespace FortniteFestival.LeaderboardScraper.MAUI;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // Resolve HomePage from DI for single-page experience wrapped in a NavigationPage so PushAsync works
        var sp = ServiceProviderHelper.ServiceProvider;
        var home = sp?.GetService(typeof(FortniteFestival.LeaderboardScraper.MAUI.Pages.HomePage)) as Page;
        if (home != null)
        {
            NavigationPage.SetHasNavigationBar(home, false); // we use custom headers
            home = new NavigationPage(home)
            {
                BarBackgroundColor = Color.FromArgb("#4B0F63"),
                BarTextColor = Colors.White
            };
        }
        var window = new Window(home ?? new ContentPage { Content = new Label { Text = "HomePage not resolved" } });

#if WINDOWS
    try
    {
        var displayInfo = DeviceDisplay.Current.MainDisplayInfo; // requires using Microsoft.Maui.Devices; (implicit global usings)
        // MAUI sizes are in device independent units (DIPs). Convert pixel width/height to DIPs.
        double density = displayInfo.Density <= 0 ? 1 : displayInfo.Density;
        double deviceWidthDip = displayInfo.Width / density;
        double deviceHeightDip = displayInfo.Height / density;
            double minWidth = Math.Min(deviceWidthDip, 360);
            double minHeight = Math.Min(deviceHeightDip, 540);
            window.MinimumWidth = minWidth;
            window.MinimumHeight = minHeight;
    }
    catch { /* non-fatal: fallback to default sizing */ }
#endif
    return window;
    }
}
