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
            // Hide navigation bar (we provide our own in-page headers / buttons)
            NavigationPage.SetHasNavigationBar(home, false);
            var nav = new NavigationPage(home)
            {
                // Make bar fully transparent & effectively invisible
                BarBackgroundColor = Colors.Transparent,
                BarTextColor = Colors.Transparent
            };
            // Ensure every subsequently pushed page also hides the built-in nav bar
            nav.Pushed += (_, e) =>
            {
                try { NavigationPage.SetHasNavigationBar(e.Page, false); } catch { }
            };
            home = nav;
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
